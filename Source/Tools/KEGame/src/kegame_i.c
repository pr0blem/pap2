

/* this ALWAYS GENERATED file contains the IIDs and CLSIDs */

/* link this file in with the server and any clients */


 /* File created by MIDL compiler version 6.00.0366 */
/* at Thu Sep 18 13:23:57 2008
 */
/* Compiler settings for .\kegame.idl:
    Oicf, W1, Zp8, env=Win32 (32b run)
    protocol : dce , ms_ext, c_ext
    error checks: allocation ref bounds_check enum stub_data 
    VC __declspec() decoration level: 
         __declspec(uuid()), __declspec(selectany), __declspec(novtable)
         DECLSPEC_UUID(), MIDL_INTERFACE()
*/
//@@MIDL_FILE_HEADING(  )

#pragma warning( disable: 4049 )  /* more than 64k source lines */


#ifdef __cplusplus
extern "C"{
#endif 


#include <rpc.h>
#include <rpcndr.h>

#ifdef _MIDL_USE_GUIDDEF_

#ifndef INITGUID
#define INITGUID
#include <guiddef.h>
#undef INITGUID
#else
#include <guiddef.h>
#endif

#define MIDL_DEFINE_GUID(type,name,l,w1,w2,b1,b2,b3,b4,b5,b6,b7,b8) \
        DEFINE_GUID(name,l,w1,w2,b1,b2,b3,b4,b5,b6,b7,b8)

#else // !_MIDL_USE_GUIDDEF_

#ifndef __IID_DEFINED__
#define __IID_DEFINED__

typedef struct _IID
{
    unsigned long x;
    unsigned short s1;
    unsigned short s2;
    unsigned char  c[8];
} IID;

#endif // __IID_DEFINED__

#ifndef CLSID_DEFINED
#define CLSID_DEFINED
typedef IID CLSID;
#endif // CLSID_DEFINED

#define MIDL_DEFINE_GUID(type,name,l,w1,w2,b1,b2,b3,b4,b5,b6,b7,b8) \
        const type name = {l,w1,w2,{b1,b2,b3,b4,b5,b6,b7,b8}}

#endif !_MIDL_USE_GUIDDEF_

MIDL_DEFINE_GUID(IID, IID_IGame,0x57C7866D,0xBD22,0x4BA3,0x9F,0x96,0xDD,0x34,0xFA,0x82,0xC6,0xAF);


MIDL_DEFINE_GUID(IID, LIBID_kegameLib,0x97D67453,0xF400,0x4598,0x9D,0xD2,0x9E,0xAC,0xFC,0x73,0x8C,0x53);


MIDL_DEFINE_GUID(IID, DIID__IGameEvents,0x3EA707FF,0xC213,0x4248,0xAA,0x20,0x0F,0x1D,0xCF,0x65,0x34,0x3D);


MIDL_DEFINE_GUID(CLSID, CLSID_Game,0x19CC8AC3,0x7B26,0x40FF,0x83,0x89,0xAB,0x24,0x60,0xE6,0x47,0xA9);

#undef MIDL_DEFINE_GUID

#ifdef __cplusplus
}
#endif



