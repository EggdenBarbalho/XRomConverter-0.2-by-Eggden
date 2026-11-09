#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include <set>
#include <mutex>
#include "MinHook.h"

namespace recommended_bridge {
struct Entry{int pv,slot,character,cos;};
std::vector<Entry> entries;
std::set<std::pair<int,int>> logged;
std::mutex log_mutex;
std::wstring directory;
HMODULE own_module{};
using Getter=int(*)(const void*,int,int);
Getter original{};
bool read(uintptr_t address,void* target,size_t size){SIZE_T received=0;return ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<const void*>(address),target,size,&received)&&received==size;}
template<class T>bool read_value(uintptr_t address,T& value){return read(address,&value,sizeof(value));}
void event(const char* name,int status=0){
 std::lock_guard<std::mutex> lock(log_mutex);
 if(auto f=_wfopen((directory+L"recommended-bridge.jsonl").c_str(),L"ab")){std::fprintf(f,"{\"event\":\"%s\",\"status\":%d}\n",name,status);std::fclose(f);}
}
bool load_entries(){
 auto f=_wfopen((directory+L"recommendations.tsv").c_str(),L"rb");if(!f)return false;
 std::string data;char buffer[4096];size_t count;
 while((count=std::fread(buffer,1,sizeof(buffer),f))){data.append(buffer,count);if(data.size()>65536){std::fclose(f);return false;}}
 std::fclose(f);std::istringstream stream(data);std::string line;std::set<std::pair<int,int>> unique;
 std::vector<Entry> candidate;
 while(std::getline(stream,line)){
  if(line.empty()||line[0]=='#'||line=="\r")continue;
  std::istringstream row(line);Entry entry{};std::string extra;
  if(!(row>>entry.pv>>entry.slot>>entry.character>>entry.cos)||(row>>extra))return false;
  if(entry.pv<1||entry.pv>9999||entry.slot<0||entry.slot>=6||entry.cos<0||entry.cos>65535)return false;
  if(entry.character!=0&&entry.character!=1&&entry.character!=2&&entry.character!=3&&entry.character!=6&&entry.character!=7)return false;
  if(!unique.insert({entry.pv,entry.slot}).second)return false;
  candidate.push_back(entry);
 }
 if(candidate.empty())return false;
 entries=std::move(candidate);
 return true;
}
int resolve(const void* pv,int slot,int original_cos,bool logging){
 if(slot<0||slot>=6)return original_cos;
 const auto address=reinterpret_cast<uintptr_t>(pv);int id{};
 if(!read_value(address,id))return original_cos;
 const Entry* selected=nullptr;
 for(const auto& e:entries)if(e.pv==id&&e.slot==slot){selected=&e;break;}
 if(!selected)return original_cos;
 uintptr_t begin{},end{};
 if(!read_value(address+0x98,begin)||!read_value(address+0xA0,end)||end<begin||!begin)return original_cos;
 const auto size=end-begin;if(size%0x44||size>6*0x44||size<=static_cast<uintptr_t>(slot)*0x44)return original_cos;
 int type{},character{};const auto performer=begin+static_cast<uintptr_t>(slot)*0x44;
 if(!read_value(performer,type)||!read_value(performer+4,character)||type!=0||character!=selected->character)return original_cos;
 if(logging){
  std::lock_guard<std::mutex> lock(log_mutex);
  if(logged.insert({id,slot}).second)if(auto f=_wfopen((directory+L"recommended-bridge.jsonl").c_str(),L"ab")){
   std::fprintf(f,"{\"event\":\"recommended_costume\",\"pv\":%d,\"performer\":%d,\"character\":%d,\"original_cos\":%d,\"returned_cos\":%d}\n",id,slot,character,original_cos,selected->cos);std::fclose(f);
  }
 }
 return selected->cos;
}
int detour(const void* pv,int slot,int difficulty){
 // Preserve the native query and all its pseudo/difficulty behavior first.
 const int native_cos=original(pv,slot,difficulty);
 return resolve(pv,slot,native_cos,true);
}
bool profile(uintptr_t& target){
 const auto base=reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));IMAGE_DOS_HEADER dos{};IMAGE_NT_HEADERS64 nt{};
 if(!read_value(base,dos)||dos.e_magic!=IMAGE_DOS_SIGNATURE||dos.e_lfanew<=0||dos.e_lfanew>0x100000)return false;
 if(!read_value(base+dos.e_lfanew,nt)||nt.Signature!=IMAGE_NT_SIGNATURE||nt.FileHeader.Machine!=IMAGE_FILE_MACHINE_AMD64||nt.OptionalHeader.SizeOfImage<0x4BF000)return false;
 const unsigned char prefix[]={0x48,0x89,0x5c,0x24,0x18,0x55,0x41,0x56,0x41,0x57,0x48,0x83,0xec,0x20,0x48,0x63,0xea,0x4c,0x8b,0xf1,0x49,0x63,0xd8,0x83,0xfd,0x05,0x0f,0x87,0xe8,0x00,0x00,0x00};
 unsigned char actual[sizeof(prefix)]{};target=base+0x4BCFC0;
 if(!read(target,actual,sizeof(actual))||std::memcmp(actual,prefix,sizeof(actual)))return false;
 const unsigned char costume[]={0x8b,0x7c,0x98,0x0c,0xeb,0x03,0x8b,0x78,0x18};unsigned char actual_costume[sizeof(costume)]{};
 return read(target+0x76,actual_costume,sizeof(actual_costume))&&!std::memcmp(costume,actual_costume,sizeof(costume));
}
}

using namespace recommended_bridge;
#ifndef BRIDGE_TEST
#ifdef XROM_UNIFIED
void StartRecommended(HMODULE module){own_module=module;
#else
extern "C" __declspec(dllexport) void Init(){
#endif
 wchar_t path[32768]{};DWORD length=GetModuleFileNameW(own_module,path,32768);if(!length||length>=32768)return;
 directory=std::wstring(path,length);const auto split=directory.find_last_of(L"\\/");if(split==std::wstring::npos)return;directory.resize(split+1);
 uintptr_t target{};if(!profile(target)){event("unsupported_profile_disabled");return;}
 if(!load_entries()){event("invalid_recommendation_data_disabled");return;}
 auto status=MH_Initialize();if(status!=MH_OK&&status!=MH_ERROR_ALREADY_INITIALIZED){event("hook_initialization_failed",status);return;}
 status=MH_CreateHook(reinterpret_cast<void*>(target),reinterpret_cast<void*>(&detour),reinterpret_cast<void**>(&original));
 if(status!=MH_OK){event("hook_creation_failed",status);
#ifndef XROM_UNIFIED
MH_Uninitialize();
#endif
return;}
 status=MH_EnableHook(reinterpret_cast<void*>(target));
 if(status!=MH_OK){event("hook_activation_failed",status);MH_RemoveHook(reinterpret_cast<void*>(target));
#ifndef XROM_UNIFIED
MH_Uninitialize();
#endif
return;}
 event("recommended_getter_hook_enabled",static_cast<int>(entries.size()));
}
#ifndef XROM_UNIFIED
BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){own_module=module;DisableThreadLibraryCalls(module);}return TRUE;}
#endif
#else
#include <cassert>
int main(int argc,char** argv){
 assert(argc==2);std::string narrow=argv[1];directory.assign(narrow.begin(),narrow.end());directory+=L"/";assert(load_entries());
 alignas(8) unsigned char fake_pv[0xA8]{};alignas(8) unsigned char performers[6*0x44]{};
 uintptr_t begin=reinterpret_cast<uintptr_t>(performers),end=begin+sizeof(performers);std::memcpy(fake_pv+0x98,&begin,8);std::memcpy(fake_pv+0xA0,&end,8);
 for(const auto& e:entries){
  std::memset(performers,0,sizeof(performers));std::memcpy(fake_pv,&e.pv,4);std::memcpy(performers+e.slot*0x44+4,&e.character,4);
  assert(resolve(fake_pv,e.slot,77,false)==e.cos);
  int pseudo=3;std::memcpy(performers+e.slot*0x44,&pseudo,4);assert(resolve(fake_pv,e.slot,77,false)==77);
  pseudo=0;int extra=9;std::memcpy(performers+e.slot*0x44,&pseudo,4);std::memcpy(performers+e.slot*0x44+4,&extra,4);assert(resolve(fake_pv,e.slot,77,false)==77);
 }
 assert(resolve(nullptr,0,77,false)==77);assert(resolve(fake_pv,-1,77,false)==77);assert(resolve(fake_pv,6,77,false)==77);
 // Exercise the same hooking library and adapter on synthetic, owned memory.
 auto memory=static_cast<unsigned char*>(VirtualAlloc(nullptr,4096,MEM_COMMIT|MEM_RESERVE,PAGE_EXECUTE_READWRITE));assert(memory);
 const unsigned char toy[]={0xb8,77,0,0,0,0xc3};std::memcpy(memory,toy,sizeof(toy));
 assert(MH_Initialize()==MH_OK);assert(MH_CreateHook(memory,reinterpret_cast<void*>(&detour),reinterpret_cast<void**>(&original))==MH_OK);assert(MH_EnableHook(memory)==MH_OK);
 const auto e=entries.front();std::memset(performers,0,sizeof(performers));std::memcpy(fake_pv,&e.pv,4);std::memcpy(performers+e.slot*0x44+4,&e.character,4);
 assert(reinterpret_cast<Getter>(memory)(fake_pv,e.slot,2)==e.cos);
 assert(MH_DisableHook(memory)==MH_OK);assert(reinterpret_cast<Getter>(memory)(fake_pv,e.slot,2)==77);
 assert(MH_Uninitialize()==MH_OK);VirtualFree(memory,0,MEM_RELEASE);
 std::puts("PASS: table loading, native-layout adaptation, pseudo/extra/invalid fallback and MinHook trampoline roundtrip.");
}
#endif
