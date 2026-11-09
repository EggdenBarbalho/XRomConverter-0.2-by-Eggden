#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include "quirky_policy.hpp"
#include "runtime_character.hpp"

namespace {
HMODULE own_module{};
std::wstring log_path;
uintptr_t rob_array{},pv_array{};
bool ready{};
ULONGLONG last_poll{};
struct Snapshot { int slot_id{},presence{},runtime_character{},cos{},visible{}; uintptr_t bones{}; };
std::array<Snapshot,6> previous{};
bool have_previous{};

// ReadProcessMemory fails gracefully on unreadable pages. No game calls/hooks/writes.
bool read(uintptr_t address,void* target,size_t size) {
 SIZE_T received=0;
 return ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<const void*>(address),target,size,&received)&&received==size;
}
template<class T> bool read_value(uintptr_t address,T& value){return read(address,&value,sizeof(value));}
void message(const char* event,bool supported) {
 if(auto f=_wfopen(log_path.c_str(),L"ab")) {
  std::fprintf(f,"{\"event\":\"%s\",\"supported\":%s,\"effect_applied\":false}\n",event,supported?"true":"false");std::fclose(f);
 }
}
bool verify_profile() {
 const auto base=reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
 IMAGE_DOS_HEADER dos{};IMAGE_NT_HEADERS64 nt{};
 if(!read_value(base,dos)||dos.e_magic!=IMAGE_DOS_SIGNATURE||dos.e_lfanew<=0||dos.e_lfanew>0x100000)return false;
 if(!read_value(base+dos.e_lfanew,nt)||nt.Signature!=IMAGE_NT_SIGNATURE||nt.FileHeader.Machine!=IMAGE_FILE_MACHINE_AMD64||nt.OptionalHeader.SizeOfImage<0x178E300)return false;
 const unsigned char getter[]={0x48,0x83,0xec,0x28,0xe8,0xe7,0x00,0x00,0x00,0x84,0xc0,0x74,0x19,0x48,0x63,0xc2,0x48,0x8d,0x0d,0x79,0xe3,0x26,0x01,0x48,0x69,0xc0,0x18,0x81,0x00,0x00,0x48,0x03,0xc1};
 const unsigned char presence[]={0x48,0x63,0xc2,0x48,0x69,0xc8,0xcc,0x00,0x00,0x00,0x48,0x8d,0x05,0x1f,0xe9,0x29,0x01,0x83,0x3c,0x01,0xff,0x0f,0x95,0xc0,0xc3};
 unsigned char actual_getter[sizeof(getter)]{},actual_presence[sizeof(presence)]{};
 if(!read(base+0x4EF3C0,actual_getter,sizeof(actual_getter))||std::memcmp(getter,actual_getter,sizeof(getter))!=0)return false;
 if(!read(base+0x4EF4B0,actual_presence,sizeof(actual_presence))||std::memcmp(presence,actual_presence,sizeof(presence))!=0)return false;
 int32_t displacement{};
 std::memcpy(&displacement,actual_getter+19,4);rob_array=base+0x4EF3C0+23+displacement;
 std::memcpy(&displacement,actual_presence+13,4);pv_array=base+0x4EF4B0+17+displacement;
 return rob_array==base+0x175D750&&pv_array==base+0x178DDE0;
}
bool same(const Snapshot& a,const Snapshot& b) {
 return a.slot_id==b.slot_id&&a.presence==b.presence&&a.runtime_character==b.runtime_character&&a.cos==b.cos&&a.visible==b.visible&&a.bones==b.bones;
}
}

extern "C" __declspec(dllexport) void Init() {
 wchar_t path[32768]{};DWORD length=GetModuleFileNameW(own_module,path,32768);
 if(!length||length>=32768)return;
 log_path=std::wstring(path,length);auto split=log_path.find_last_of(L"\\/");
 if(split==std::wstring::npos)return;
 log_path.resize(split+1);log_path+=L"quirky-diagnostic.jsonl";
 ready=verify_profile();message(ready?"profile_verified_read_only":"unsupported_profile_disabled",ready);
}
extern "C" __declspec(dllexport) void OnFrame(void*) {
 if(!ready||GetTickCount64()-last_poll<500)return;
 last_poll=GetTickCount64();std::array<Snapshot,6> now{};
 for(int i=0;i<6;i++) {
  const uintptr_t address=rob_array+static_cast<uintptr_t>(i)*0x8118;
  int8_t id{};uint8_t flags{};
  if(!read_value(address,id)||!read_value(address+0x10,now[i].runtime_character)||!read_value(address+0x14,now[i].cos)||!read_value(address+0x28,now[i].bones)||!read_value(address+0x440,flags)||!read_value(pv_array+static_cast<uintptr_t>(i)*0xCC,now[i].presence)) {
   ready=false;message("read_failed_disabled",false);return;
  }
  now[i].slot_id=id;now[i].visible=flags&1;
 }
 bool changed=!have_previous;for(int i=0;i<6;i++)changed=changed||!same(now[i],previous[i]);
 if(!changed)return;
 if(auto f=_wfopen(log_path.c_str(),L"ab")) {
  std::fprintf(f,"{\"event\":\"performers\",\"tick_ms\":%llu,\"effect_applied\":false,\"pv_state_verified\":false,\"slots\":[",static_cast<unsigned long long>(last_poll));
  for(int i=0;i<6;i++) {
   const auto& s=now[i];const int character=quirky::catalog_character(s.runtime_character);
   const bool valid=s.slot_id==i&&s.presence!=-1&&character>=0&&s.cos>=0&&s.cos<=65535&&s.bones!=0;
   const bool candidate=valid&&quirky::matches(true,true,character,-1,s.cos);
   std::fprintf(f,"%s{\"performer\":%d,\"slot_id\":%d,\"presence_marker\":%d,\"runtime_character\":%d,\"catalog_character\":%d,\"cos\":%d,\"visible\":%s,\"identity_candidate_valid\":%s,\"quirky_candidate\":%s}",i?",":"",i,s.slot_id,s.presence,s.runtime_character,character,s.cos,s.visible?"true":"false",valid?"true":"false",candidate?"true":"false");
  }
  std::fprintf(f,"]}\n");std::fclose(f);
 }
 previous=now;have_previous=true;
}
BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID) {
 if(reason==DLL_PROCESS_ATTACH){own_module=module;DisableThreadLibraryCalls(module);}
 return TRUE;
}
