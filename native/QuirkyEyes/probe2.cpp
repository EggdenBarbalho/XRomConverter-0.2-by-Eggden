#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

// Preparation only: capture executable code after unpacking, not actor/heap data.
// No OnFrame, game call, write, hook or global ROB replacement.
namespace {
HMODULE own_module{};
bool read(uintptr_t p,void* buffer,size_t size){SIZE_T got{};return ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(p),buffer,size,&got)&&got==size;}
}
extern "C" __declspec(dllexport) void Init(){
 wchar_t path[32768]{};DWORD n=GetModuleFileNameW(own_module,path,32768);if(!n||n>=32768)return;
 std::wstring directory(path,n);auto slash=directory.find_last_of(L"\\/");if(slash==std::wstring::npos)return;directory.resize(slash+1);
 const auto base=reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
 IMAGE_DOS_HEADER dos{};IMAGE_NT_HEADERS64 nt{};
 if(!read(base,&dos,sizeof(dos))||dos.e_magic!=IMAGE_DOS_SIGNATURE||dos.e_lfanew<=0||dos.e_lfanew>0x100000)return;
 if(!read(base+dos.e_lfanew,&nt,sizeof(nt))||nt.Signature!=IMAGE_NT_SIGNATURE||nt.FileHeader.Machine!=IMAGE_FILE_MACHINE_AMD64||nt.OptionalHeader.SizeOfImage<0x560000)return;
 const unsigned char signature[]={0x48,0x83,0xec,0x28,0xe8,0xe7,0x00,0x00,0x00,0x84,0xc0,0x74,0x19,0x48,0x63,0xc2,0x48,0x8d,0x0d,0x79,0xe3,0x26,0x01};
 unsigned char actual[sizeof(signature)]{};
 if(!read(base+0x4EF3C0,actual,sizeof(actual))||std::memcmp(signature,actual,sizeof(actual)))return;
 constexpr uintptr_t start=0x400000;constexpr size_t length=0x160000;
 std::vector<unsigned char> bytes(length);
 // Fail closed if this executable region cannot be read in full.
 if(!read(base+start,bytes.data(),bytes.size()))return;
 auto f=_wfopen((directory+L"facial-code-runtime.bin").c_str(),L"wb");if(!f)return;
 const bool ok=std::fwrite(bytes.data(),1,bytes.size(),f)==bytes.size();std::fclose(f);
 if(auto log=_wfopen((directory+L"probe2.json").c_str(),L"wb")){
  std::fprintf(log,"{\"schema\":1,\"effectApplied\":false,\"codeOnly\":true,\"base\":\"0x%llx\",\"startRva\":\"0x400000\",\"size\":%llu,\"captureComplete\":%s}\n",static_cast<unsigned long long>(base),static_cast<unsigned long long>(length),ok?"true":"false");std::fclose(log);
 }
}
BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){own_module=module;DisableThreadLibraryCalls(module);}return TRUE;}
