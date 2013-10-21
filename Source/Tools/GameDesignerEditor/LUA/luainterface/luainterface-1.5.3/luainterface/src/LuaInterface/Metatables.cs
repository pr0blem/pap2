namespace LuaInterface
{
    using System;
    using System.IO;
    using System.Collections;
    using System.Reflection;
    using System.Diagnostics;

    /*
     * Functions used in the metatables of userdata representing
     * CLR objects
     * 
     * Author: Fabio Mascarenhas
     * Version: 1.0
     */
    class MetaFunctions
    {
        /*
         * __index metafunction for CLR objects. Implemented in Lua.
         */
        internal static string luaIndexFunction =
            "local function index(obj,name)\n" +
            "  local meta=getmetatable(obj)\n" +
            "  local cached=meta.cache[name]\n" +
            "  if cached~=nil  then\n" +
            "    return cached\n" +
            "  else\n" +
            "    local value,isFunc=get_object_member(obj,name)\n" +
            "    if isFunc then\n" +
            "      meta.cache[name]=value\n" +
            "    end\n" +
            "    return value\n" +
            "  end\n" +
            "end\n" +
            "return index";

        private ObjectTranslator translator;
        private Hashtable memberCache = new Hashtable();
        internal LuaCSFunction gcFunction, indexFunction, newindexFunction,
            baseIndexFunction, classIndexFunction, classNewindexFunction,
            execDelegateFunction, callConstructorFunction, toStringFunction;

        public MetaFunctions(ObjectTranslator translator)
        {
            this.translator = translator;
            gcFunction = new LuaCSFunction(this.collectObject);
            toStringFunction = new LuaCSFunction(this.toString);
            indexFunction = new LuaCSFunction(this.getMethod);
            newindexFunction = new LuaCSFunction(this.setFieldOrProperty);
            baseIndexFunction = new LuaCSFunction(this.getBaseMethod);
            callConstructorFunction = new LuaCSFunction(this.callConstructor);
            classIndexFunction = new LuaCSFunction(this.getClassMethod);
            classNewindexFunction = new LuaCSFunction(this.setClassFieldOrProperty);
            execDelegateFunction = new LuaCSFunction(this.runFunctionDelegate);
        }

        /*
         * __call metafunction of CLR delegates, retrieves and calls the delegate.
         */
        private int runFunctionDelegate(IntPtr luaState)
        {
            LuaCSFunction func = (LuaCSFunction)translator.getRawNetObject(luaState, 1);
            LuaDLL.lua_remove(luaState, 1);
            return func(luaState);
        }
        /*
         * __gc metafunction of CLR objects.
         */
        private int collectObject(IntPtr luaState)
        {
            int udata = LuaDLL.luanet_rawnetobj(luaState, 1);
            if (udata != -1)
            {
                translator.collectObject(udata);
            }
            else
            {
                // Debug.WriteLine("not found: " + udata);
            }
            return 0;
        }
        /*
         * __tostring metafunction of CLR objects.
         */
        private int toString(IntPtr luaState)
        {
            object obj = translator.getRawNetObject(luaState, 1);
            if (obj != null)
            {
                translator.push(luaState, obj.ToString() + ": " + obj.GetHashCode());
            }
            else LuaDLL.lua_pushnil(luaState);
            return 1;
        }


        /// <summary>
        /// Debug tool to dump the lua stack
        /// </summary>
        /// FIXME, move somewhere else
        public static void dumpStack(ObjectTranslator translator, IntPtr luaState)
        {
            int depth = LuaDLL.lua_gettop(luaState);

            Debug.WriteLine("lua stack depth: " + depth);
            for (int i = 1; i <= depth; i++)
            {
                LuaTypes type = LuaDLL.lua_type(luaState, i);
                // we dump stacks when deep in calls, calling typename while the stack is in flux can fail sometimes, so manually check for key types
                string typestr = (type == LuaTypes.LUA_TTABLE) ? "table" : LuaDLL.lua_typename(luaState, type);

                string strrep = LuaDLL.lua_tostring(luaState, i);
                if (type == LuaTypes.LUA_TUSERDATA)
                {
                    object obj = translator.getRawNetObject(luaState, i);
                    strrep = obj.ToString();
                }

                Debug.Print("{0}: ({1}) {2}", i, typestr, strrep);
            }
        }


        /*
         * Called by the __index metafunction of CLR objects in case the
         * method is not cached or it is a field/property/event.
         * Receives the object and the member name as arguments and returns
         * either the value of the member or a delegate to call it.
         * If the member does not exist returns nil.
         */
        private int getMethod(IntPtr luaState)
        {
            object obj = translator.getRawNetObject(luaState, 1);
            if (obj == null)
            {
                translator.throwError(luaState, "trying to index an invalid object reference");
                LuaDLL.lua_pushnil(luaState);
                return 1;
            }

            object index = translator.getObject(luaState, 2);
            Type indexType = index.GetType();

            string methodName = index as string;        // will be null if not a string arg
            Type objType = obj.GetType();

            // Handle the most common case, looking up the method by name
            if (methodName != null && isMemberPresent(objType, methodName))
                return getMember(luaState, objType, obj, methodName, BindingFlags.Instance);

            // Try to access by array if the type is right and index is an int (lua numbers always come across as double)
            if (obj is Array && index is double)
            {
                object[] arr = (object[])obj;

                translator.push(luaState, arr[(int)((double)index)]);
            } 
            else
            {
                // Try to use get_Item to index into this .net object
                Type[] argTypes = new Type[1];
                argTypes[0] = indexType;
                MethodInfo getter = objType.GetMethod("get_Item", argTypes);

                if (index is double)
                {
                    // For numbers we always prefer to pass ints into our accessor if possible (if the accessor
                    // is really built for doubles it should cast back correctly
                    double d = (double)index;

                    if (d == Math.Round(d))
                    {
                        bool convertToInt = true;

                        // If we already found a getter, it might be a version expecting an 'object' parameter
                        // in that case we'll want to convert to an int 
                        if (getter != null)
                        {
                            ParameterInfo[] actualParms = getter.GetParameters();

                            convertToInt = actualParms[0].ParameterType != typeof(double);
                        }

                        if(convertToInt)
                            index = (int)d;     // convert the index to a true (but boxed) int
                    }

                    // If we can't find a double based indexer, fall back to an int based index
                    if (getter == null && index is int)
                    {
                        argTypes[0] = typeof(int);
                        getter = objType.GetMethod("get_Item", argTypes);
                    }
                }

                if (getter == null)
                {
                    translator.throwError(luaState, "method not found (or no indexer): " + index);

                    LuaDLL.lua_pushnil(luaState);
                }

                object[] args = new object[1];

                // Just call the indexer - if out of bounds an exception will happen
                args[0] = index;
                try
                {
                    object result = getter.Invoke(obj, args);
                    translator.push(luaState, result);
                }
                catch (Exception e)
                {
                    translator.throwError(luaState, "exception while indexing: " + e);

                    LuaDLL.lua_pushnil(luaState);
                }
            }

            LuaDLL.lua_pushboolean(luaState, false);
            return 2;
        }


        /*
         * __index metafunction of base classes (the base field of Lua tables).
         * Adds a prefix to the method name to call the base version of the method.
         */
        private int getBaseMethod(IntPtr luaState)
        {
            object obj = translator.getRawNetObject(luaState, 1);
            if (obj == null)
            {
                translator.throwError(luaState, "trying to index an invalid object reference");
                LuaDLL.lua_pushnil(luaState);
                LuaDLL.lua_pushboolean(luaState, false);
                return 2;
            }
            string methodName = LuaDLL.lua_tostring(luaState, 2);
            if (methodName == null)
            {
                LuaDLL.lua_pushnil(luaState);
                LuaDLL.lua_pushboolean(luaState, false);
                return 2;
            }
            getMember(luaState, obj.GetType(), obj, "__luaInterface_base_" + methodName, BindingFlags.Instance);
            LuaDLL.lua_settop(luaState, -2);
            if (LuaDLL.lua_type(luaState, -1) == LuaTypes.LUA_TNIL)
            {
                LuaDLL.lua_settop(luaState, -2);
                return getMember(luaState, obj.GetType(), obj, methodName, BindingFlags.Instance);
            }
            LuaDLL.lua_pushboolean(luaState, false);
            return 2;
        }


        /// <summary>
        /// Does this method exist as either an instance or static?
        /// </summary>
        /// <param name="objType"></param>
        /// <param name="methodName"></param>
        /// <returns></returns>
        bool isMemberPresent(IReflect objType, string methodName)
        {
            object cachedMember = checkMemberCache(memberCache, objType, methodName);

            if (cachedMember != null)
                return true;

            MemberInfo[] members = objType.GetMember(methodName, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return (members.Length > 0);
        }

        /*
         * Pushes the value of a member or a delegate to call it, depending on the type of
         * the member. Works with static or instance members.
         * Uses reflection to find members, and stores the reflected MemberInfo object in
         * a cache (indexed by the type of the object and the name of the member).
         */
        private int getMember(IntPtr luaState, IReflect objType, object obj, string methodName, BindingFlags bindingType)
        {
            bool implicitStatic = false;
            MemberInfo member = null;
            object cachedMember = checkMemberCache(memberCache, objType, methodName);
            //object cachedMember=null;
            if (cachedMember is LuaCSFunction)
            {
                translator.pushFunction(luaState, (LuaCSFunction)cachedMember);
                translator.push(luaState, true);
                return 2;
            }
            else if (cachedMember != null)
            {
                member = (MemberInfo)cachedMember;
            }
            else
            {
                MemberInfo[] members = objType.GetMember(methodName, bindingType | BindingFlags.Public | BindingFlags.NonPublic);
                if (members.Length > 0)
                    member = members[0];
                else
                {
                    // If we can't find any suitable instance members, try to find them as statics - but we only want to allow implicit static
                    // lookups for fields/properties/events -kevinh
                    members = objType.GetMember(methodName, bindingType | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    if (members.Length > 0)
                    {
                        member = members[0];
                        implicitStatic = true;
                    }
                }
            }
            if (member != null)
            {
                if (member.MemberType == MemberTypes.Field)
                {
                    FieldInfo field = (FieldInfo)member;
                    if (cachedMember == null) setMemberCache(memberCache, objType, methodName, member);
                    try
                    {
                        translator.push(luaState, field.GetValue(obj));
                    }
                    catch
                    {
                        LuaDLL.lua_pushnil(luaState);
                    }
                }
                else if (member.MemberType == MemberTypes.Property)
                {
                    PropertyInfo property = (PropertyInfo)member;
                    if (cachedMember == null) setMemberCache(memberCache, objType, methodName, member);
                    try
                    {
                        object val = property.GetValue(obj, null);

                        translator.push(luaState, val);
                    }
                    catch (ArgumentException)
                    {
                        // If we can't find the getter in our class, recurse up to the base class and see
                        // if they can help.

                        if (objType is Type && !(((Type)objType) == typeof(object)))
                            return getMember(luaState, ((Type)objType).BaseType, obj, methodName, bindingType);
                        else
                            LuaDLL.lua_pushnil(luaState);
                    }
                    catch
                    {
                        LuaDLL.lua_pushnil(luaState);
                    }
                }
                else if (member.MemberType == MemberTypes.Event)
                {
                    EventInfo eventInfo = (EventInfo)member;
                    if (cachedMember == null) setMemberCache(memberCache, objType, methodName, member);
                    translator.push(luaState, new RegisterEventHandler(translator.pendingEvents, obj, eventInfo));
                }
                else if(!implicitStatic)
                {
                    if (member.MemberType == MemberTypes.NestedType)
                    {
                        // kevinh - added support for finding nested types

                        // cache us
                        if (cachedMember == null) setMemberCache(memberCache, objType, methodName, member);

                        // Find the name of our class
                        string name = member.Name;
                        Type dectype = member.DeclaringType;

                        // Build a new long name and try to find the type by name
                        string longname = dectype.FullName + "+" + name;
                        Type nestedType = translator.FindType(longname);

                        translator.pushType(luaState, nestedType);
                    }
                    else
                    {
                        // Member type must be 'method'
                        LuaCSFunction wrapper = new LuaCSFunction((new LuaMethodWrapper(translator, objType, methodName, bindingType)).call);
                        if (cachedMember == null) setMemberCache(memberCache, objType, methodName, wrapper);
                        translator.pushFunction(luaState, wrapper);
                        translator.push(luaState, true);
                        return 2;
                    }
                }
                else
                {
                    // If we reach this point we found a static method, but can't use it in this context because the user passed in an instance
                    translator.throwError(luaState, "can't pass instance to static method " + methodName);

                    LuaDLL.lua_pushnil(luaState);
                }
            }
            else
            {
                // kevinh - we want to throw an exception because meerly returning 'nil' in this case
                // is not sufficient.  valid data members may return nil and therefore there must be some
                // way to know the member just doesn't exist.

                translator.throwError(luaState, "unknown member name " + methodName);

                LuaDLL.lua_pushnil(luaState);
            }

            // push false because we are NOT returning a function (see luaIndexFunction)
            translator.push(luaState, false);
            return 2;
        }
        /*
         * Checks if a MemberInfo object is cached, returning it or null.
         */
        private object checkMemberCache(Hashtable memberCache, IReflect objType, string memberName)
        {
            Hashtable members = (Hashtable)memberCache[objType];
            if (members != null)
                return members[memberName];
            else
                return null;
        }
        /*
         * Stores a MemberInfo object in the member cache.
         */
        private void setMemberCache(Hashtable memberCache, IReflect objType, string memberName, object member)
        {
            Hashtable members = (Hashtable)memberCache[objType];
            if (members == null)
            {
                members = new Hashtable();
                memberCache[objType] = members;
            }
            members[memberName] = member;
        }
        /*
         * __newindex metafunction of CLR objects. Receives the object,
         * the member name and the value to be stored as arguments. Throws
         * and error if the assignment is invalid.
         */
        private int setFieldOrProperty(IntPtr luaState)
        {
            if (LuaDLL.lua_isnumber(luaState, 2))
            {
                object target = translator.getRawNetObject(luaState, 1);
                if (target == null)
                {
                    translator.throwError(luaState, "trying to index and invalid object reference");
                    return 0;
                }
                int index = (int)LuaDLL.lua_tonumber(luaState, 2);
                try
                {
                    if (target is Array)
                    {
                        object[] arr = (object[])target;
                        object val = translator.getAsType(luaState, 3, arr.GetType().GetElementType());
                        arr[index] = val;
                    }
                    else
                    {
                        // Try to see if we have an array accessor
                        Type type = target.GetType();

                        MethodInfo setter = type.GetMethod("set_Item");
                        if (setter == null)
                            translator.throwError(luaState, "this object does not support indexed setting");
                        else
                        {
                            ParameterInfo [] args = setter.GetParameters();
                            Type valueType = args[1].ParameterType;

                            // The new val ue the user specified 
                            object val = translator.getAsType(luaState, 3, valueType);

                            object[] methodArgs = new object[2];

                            // Just call the indexer - if out of bounds an exception will happen
                            methodArgs[0] = index;
                            methodArgs[1] = val;

                            setter.Invoke(target, methodArgs);
                        }
                    }
                }
                catch (Exception e)
                {
                    translator.throwError(luaState, e);
                }
                return 0;
            }
            else
            {
                object target = translator.getRawNetObject(luaState, 1);
                if (target == null)
                {
                    translator.throwError(luaState, "trying to index an invalid object reference");
                    return 0;
                }
                return setMember(luaState, target.GetType(), target, BindingFlags.Instance);
            }
        }
        /*
         * Writes to fields or properties, either static or instance. Throws an error
         * if the operation is invalid.
         */
        private int setMember(IntPtr luaState, IReflect targetType, object target, BindingFlags bindingType)
        {
            string fieldName = LuaDLL.lua_tostring(luaState, 2);
            if (fieldName == null)
            {
                translator.throwError(luaState, "field or property does not exist");
                return 0;
            }
            MemberInfo member = (MemberInfo)checkMemberCache(memberCache, targetType, fieldName);
            if (member == null)
            {
                MemberInfo[] members = targetType.GetMember(fieldName, bindingType | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.GetField);
                if (members.Length > 0)
                {
                    member = members[0];
                    setMemberCache(memberCache, targetType, fieldName, member);
                }
                else
                {
                    translator.throwError(luaState, "field or property does not exist");
                    return 0;
                }
            }
            if (member.MemberType == MemberTypes.Field)
            {
                FieldInfo field = (FieldInfo)member;
                object val = translator.getAsType(luaState, 3, field.FieldType);
                try
                {
                    field.SetValue(target, val);
                }
                catch (Exception e)
                {
                    translator.throwError(luaState, e);
                }
                return 0;
            }
            else if (member.MemberType == MemberTypes.Property)
            {
                PropertyInfo property = (PropertyInfo)member;
                object val = translator.getAsType(luaState, 3, property.PropertyType);
                try
                {
                    property.SetValue(target, val, null);
                }
                catch (Exception e)
                {
                    translator.throwError(luaState, e);
                }
                return 0;
            }
            translator.throwError(luaState, "field or property does not exist");
            return 0;
        }
        /*
         * __index metafunction of type references, works on static members.
         */
        private int getClassMethod(IntPtr luaState)
        {
            IReflect klass;
            object obj = translator.getRawNetObject(luaState, 1);
            if (obj == null || !(obj is IReflect))
            {
                translator.throwError(luaState, "trying to index an invalid type reference");
                LuaDLL.lua_pushnil(luaState);
                return 1;
            }
            else klass = (IReflect)obj;
            if (LuaDLL.lua_isnumber(luaState, 2))
            {
                int size = (int)LuaDLL.lua_tonumber(luaState, 2);
                translator.push(luaState, Array.CreateInstance(klass.UnderlyingSystemType, size));
                return 1;
            }
            else
            {
                string methodName = LuaDLL.lua_tostring(luaState, 2);
                if (methodName == null)
                {
                    LuaDLL.lua_pushnil(luaState);
                    return 1;
                }
                else return getMember(luaState, klass, null, methodName, BindingFlags.FlattenHierarchy | BindingFlags.Static);
            }
        }
        /*
         * __newindex function of type references, works on static members.
         */
        private int setClassFieldOrProperty(IntPtr luaState)
        {
            IReflect target;
            object obj = translator.getRawNetObject(luaState, 1);
            if (obj == null || !(obj is IReflect))
            {
                translator.throwError(luaState, "trying to index an invalid type reference");
                return 0;
            }
            else target = (IReflect)obj;
            return setMember(luaState, target, null, BindingFlags.FlattenHierarchy | BindingFlags.Static);
        }
        /*
         * __call metafunction of type references. Searches for and calls
         * a constructor for the type. Returns nil if the constructor is not
         * found or if the arguments are invalid. Throws an error if the constructor
         * generates an exception.
         */
        private int callConstructor(IntPtr luaState)
        {
            MethodCache validConstructor = new MethodCache();
            IReflect klass;
            object obj = translator.getRawNetObject(luaState, 1);
            if (obj == null || !(obj is IReflect))
            {
                translator.throwError(luaState, "trying to call constructor on an invalid type reference");
                LuaDLL.lua_pushnil(luaState);
                return 1;
            }
            else klass = (IReflect)obj;
            LuaDLL.lua_remove(luaState, 1);
            ConstructorInfo[] constructors = klass.UnderlyingSystemType.GetConstructors();
            foreach (ConstructorInfo constructor in constructors)
            {
                bool isConstructor = matchParameters(luaState, constructor, ref validConstructor);
                if (isConstructor)
                {
                    try
                    {
                        translator.push(luaState, constructor.Invoke(validConstructor.args));
                    }
                    catch (TargetInvocationException e)
                    {
                        translator.throwError(luaState, e.InnerException);
                        LuaDLL.lua_pushnil(luaState);
                    }
                    catch
                    {
                        LuaDLL.lua_pushnil(luaState);
                    }
                    return 1;
                }
            }

            translator.throwError(luaState, "can't find constructor to match arguments");
            LuaDLL.lua_pushnil(luaState);
            return 1;
        }
        /*
         * Matches a method against its arguments in the Lua stack. Returns
         * if the match was succesful. It it was also returns the information
         * necessary to invoke the method.
         */
        internal bool matchParameters(IntPtr luaState, MethodBase method, ref MethodCache methodCache)
        {
            ExtractValue extractValue;
            bool isMethod = true;
            ParameterInfo[] paramInfo = method.GetParameters();
            int currentLuaParam = 1;
            int nLuaParams = LuaDLL.lua_gettop(luaState);
            ArrayList paramList = new ArrayList();
            ArrayList outList = new ArrayList();
            ArrayList argTypes = new ArrayList();
            foreach (ParameterInfo currentNetParam in paramInfo)
            {
                if (!currentNetParam.IsIn && currentNetParam.IsOut)  // Skips out params
                {
                    outList.Add(paramList.Add(null));
                }
                else if (currentLuaParam > nLuaParams) // Adds optional parameters
                {
                    if (currentNetParam.IsOptional)
                    {
                        paramList.Add(currentNetParam.DefaultValue);
                    }
                    else
                    {
                        isMethod = false;
                        break;
                    }
                }
                else if ((extractValue = translator.typeChecker.checkType(luaState, currentLuaParam, currentNetParam.ParameterType)) != null)  // Type checking
                {
                    int index = paramList.Add(extractValue(luaState, currentLuaParam));
                    MethodArgs methodArg = new MethodArgs();
                    methodArg.index = index;
                    methodArg.extractValue = extractValue;
                    argTypes.Add(methodArg);
                    if (currentNetParam.ParameterType.IsByRef)
                        outList.Add(index);
                    currentLuaParam++;
                }  // Type does not match, ignore if the parameter is optional
                else if (currentNetParam.IsOptional)
                {
                    paramList.Add(currentNetParam.DefaultValue);
                }
                else  // No match
                {
                    isMethod = false;
                    break;
                }
            }
            if (currentLuaParam != nLuaParams + 1) // Number of parameters does not match
                isMethod = false;
            if (isMethod)
            {
                methodCache.args = paramList.ToArray();
                methodCache.cachedMethod = method;
                methodCache.outList = (int[])outList.ToArray(typeof(int));
                methodCache.argTypes = (MethodArgs[])argTypes.ToArray(typeof(MethodArgs));
            }
            return isMethod;
        }
    }
}