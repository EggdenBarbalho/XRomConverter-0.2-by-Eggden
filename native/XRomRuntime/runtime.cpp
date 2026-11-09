#define WIN32_LEAN_AND_MEAN
#include <windows.h>
void StartRecommended(HMODULE);
void StartQuirky(HMODULE);
static HMODULE module{};
extern "C" __declspec(dllexport) void Init(){
 static bool initialized=false;if(initialized)return;initialized=true;
 wchar_t path[32768]{};DWORD length=GetModuleFileNameW(module,path,32768);if(!length||length>=32768)return;
 for(DWORD i=length;i>0;i--)if(path[i-1]==L'\\'||path[i-1]==L'/'){path[i]=0;break;}
 wchar_t ini[32768]{};if(lstrlenW(path)+16>=32768)return;lstrcpyW(ini,path);lstrcatW(ini,L"xrom-runtime.ini");
 if(GetPrivateProfileIntW(L"RecommendedModules",L"enabled",1,ini))StartRecommended(module);
 if(GetPrivateProfileIntW(L"QuirkyEyes",L"enabled",1,ini))StartQuirky(module);
}
BOOL WINAPI DllMain(HINSTANCE m,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){module=m;DisableThreadLibraryCalls(m);}return TRUE;}
