#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <fstream>
#include <sstream>
#include <filesystem>
#include <mutex>
#include <set>
#include <array>
#include "MinHook.h"
#include "runtime_curves.hpp"
#include "native_profile.hpp"
#include "native_blocks.hpp"
#include "quirky_policy.hpp"
#include "runtime_character.hpp"

namespace quirky_effect {
using Partial=void(*)(void*,void*,const void*,int);
using Interpolate=void(*)(void*,float,uint32_t,uint32_t);
Partial original_partial{};Interpolate original_interpolate{};
quirky_runtime::Data data;
HMODULE own_module{};std::wstring directory;uintptr_t actor_array{};bool enabled=true;
std::mutex log_mutex;std::set<std::tuple<int,int,uint32_t>> logged;
bool read(uintptr_t p,void* target,size_t n){SIZE_T got{};return ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(p),target,n,&got)&&got==n;}
template<class T>bool value(uintptr_t p,T& x){return read(p,&x,sizeof(x));}
bool text(uintptr_t p,std::string& s){char bytes[128]{};if(!p||!read(p,bytes,sizeof(bytes)))return false;auto end=static_cast<char*>(std::memchr(bytes,0,sizeof(bytes)));if(!end)return false;s.assign(bytes,end);return true;}
void event(const char* name,int status=0){std::lock_guard<std::mutex> lock(log_mutex);if(auto f=_wfopen((directory+L"quirky-runtime.jsonl").c_str(),L"ab")){std::fprintf(f,"{\"event\":\"%s\",\"status\":%d}\n",name,status);std::fclose(f);}}
void trace(const char* stage,int slot=-1,int cos=-1,uint32_t motion=0,uintptr_t detail=0){
 try{std::lock_guard<std::mutex> lock(log_mutex);
 static std::set<std::tuple<std::string,int,int,uint32_t,uintptr_t>> seen;
 if(seen.size()>=96||!seen.insert({stage,slot,cos,motion,detail}).second)return;
 if(auto f=_wfopen((directory+L"quirky-runtime.jsonl").c_str(),L"ab")){
 std::fprintf(f,"{\"event\":\"selection_trace\",\"stage\":\"%s\",\"slot\":%d,\"cos\":%d,\"motion\":%u,\"detail\":%llu}\n",stage,slot,cos,motion,static_cast<unsigned long long>(detail));std::fclose(f);}
 }catch(...){}
}
void inspect_blocks(uintptr_t begin,uintptr_t end){
 static bool sampled=false;if(sampled)return;sampled=true;
 try{std::lock_guard<std::mutex> lock(log_mutex);auto f=_wfopen((directory+L"quirky-blocks.jsonl").c_str(),L"ab");if(!f)return;
 for(uintptr_t b=begin;b<end;b+=0x2F0){uintptr_t node{};uint32_t id{},offset{},count{};value(b+8,id);value(b+0x18,offset);value(b+0x1C,count);value(b+0x2C8,node);
 std::fprintf(f,"{\"block\":%u,\"keyOffset\":%u,\"keyCount\":%u,\"nodePresent\":%s,\"strings\":[",id,offset,count,node?"true":"false");bool comma=false;
 if(node)for(unsigned o=0;o<0x108;o+=8){uintptr_t ptr{};std::string name;if(!value(node+o,ptr)||!text(ptr,name)||name.empty()||name.size()>80)continue;bool valid=true;for(unsigned char c:name)if(!(c=='_'||c=='.'||c=='-'||(c>='0'&&c<='9')||(c>='a'&&c<='z')||(c>='A'&&c<='Z')))valid=false;if(!valid)continue;
 std::fprintf(f,"%s{\"offset\":%u,\"name\":\"%s\"}",comma?",":"",o,name.c_str());comma=true;}
 std::fprintf(f,"]}\n");}std::fclose(f);
 }catch(...){}
}
std::set<std::pair<int,int>> allocated_quirky;
bool allocation_loaded=false;
bool load_policy(){
 std::ifstream f(std::filesystem::path(directory+L"quirky-modules.tsv"));if(!f)return false;
 std::string line;std::set<std::pair<int,int>> rows;size_t total=0;
 while(std::getline(f,line)){if((total+=line.size())>65536)return false;if(line.empty()||line[0]=='#')continue;std::istringstream row(line);int ch{},cos{};std::string extra;
 if(!(row>>ch>>cos)||(row>>extra)||ch<0||ch>5||cos<0||cos>65535||!rows.insert({ch,cos}).second)return false;}
 allocated_quirky=std::move(rows);allocation_loaded=true;return true;
}
struct Block {uintptr_t address,node;std::string name;};
struct Cache {uintptr_t end{};std::vector<Block> blocks;};
thread_local std::map<std::pair<uintptr_t,int>,Cache> caches;
struct Context {void* key{};const quirky_runtime::Motion* motion{};const Cache* cache{};int slot=-1,cos=-1;uint32_t motion_id{};};
thread_local Context context;

Context select(void* partial,void* vector){
 Context result;if(!enabled)return result;
 auto p=reinterpret_cast<uintptr_t>(partial);int character=-1;
 for(int slot=0;slot<6;slot++){
  uintptr_t actor=actor_array+static_cast<uintptr_t>(slot)*0x8118,bones{};int8_t id{};int native{},cos{};
  if(!value(actor,id)||id!=slot||!value(actor+0x28,bones)||!bones||(p!=bones+0xC8&&p!=bones+0x640))continue;
  if(!value(actor+0x10,native)||!value(actor+0x14,cos))return result;
  character=quirky::catalog_character(native);
  if(character<0||(allocation_loaded?allocated_quirky.count({character,cos})==0:!quirky::matches(true,enabled,character,-1,cos)))return result;
  result.slot=slot;result.cos=cos;break;
 }
 if(result.slot<0){trace("no_quirky_owner");return {};}
 trace("quirky_owner",result.slot,result.cos);
 uint32_t motion{};if(!value(p+0x98,motion))return {};
 auto found=data.find({character,motion});if(found==data.end()){trace("motion_not_mapped",result.slot,result.cos,motion);return {};}
 trace("motion_mapped",result.slot,result.cos,motion);
 uintptr_t begin{},end{};auto v=reinterpret_cast<uintptr_t>(vector);
 if(!value(v,begin)||!value(v+8,end)||!begin||end<begin||(end-begin)%0x2F0||(end-begin)/0x2F0>1024)return {};
 if(caches.size()>32&&!context.motion)caches.clear();
 auto& cache=caches[{begin,character}];
 if(cache.end!=end||cache.blocks.empty()){
  cache.end=end;cache.blocks.clear();
  for(uintptr_t block=begin;block<end;block+=0x2F0){
   uintptr_t node{},name{};std::string s;
   if(!value(block+0x2C8,node)||!node)continue;
   bool named=value(node,name)&&text(name,s)&&found->second.bones.count(s);
   if(!named&&(end-begin)/0x2F0==quirky_blocks::size(character)){
    uint32_t id{},count{};auto table=quirky_blocks::table(character);
    if(value(block+8,id)&&id==static_cast<uint32_t>((block-begin)/0x2F0)&&id<quirky_blocks::size(character)&&value(block+0x1C,count)&&count==table[id].count&&found->second.bones.count(table[id].name))s=table[id].name;else continue;
   }else if(!named)continue;
   cache.blocks.push_back({block,node,s});
  }
 }
 if(cache.blocks.empty()){inspect_blocks(begin,end);trace("no_eye_blocks",result.slot,result.cos,motion,(end-begin)/0x2F0);return {};}
 trace("eye_blocks_ready",result.slot,result.cos,motion,cache.blocks.size());
 result.key=reinterpret_cast<void*>(p+0x38);result.motion=&found->second;result.cache=&cache;result.motion_id=motion;return result;
}

bool apply(void* key,float frame,uint32_t offset,uint32_t count){
 if(!context.motion||!context.cache||context.key!=key||!std::isfinite(frame)||(count!=3&&count!=6))return false;
 uintptr_t selected{};const quirky_runtime::Bone* curves=nullptr;
 for(const auto& block:context.cache->blocks){
  uint32_t key_offset{},key_count{};uintptr_t node{};
  if(value(block.address+0x18,key_offset)&&key_offset==offset&&value(block.address+0x1C,key_count)&&key_count==count&&value(block.address+0x2C8,node)&&node==block.node){
   auto found=context.motion->bones.find(block.name);if(found!=context.motion->bones.end()){curves=&found->second;selected=block.address;break;}
  }
 }
 if(!selected||!curves||curves->curves.size()!=count)return false;
 const auto p=reinterpret_cast<uintptr_t>(key);uintptr_t begin{},end{};int mode{};
 if(!value(p+0x38,begin)||!value(p+0x40,end)||end<begin||!begin||(end-begin)%4||(end-begin)/4>16384||static_cast<uint64_t>(offset)+count>(end-begin)/4)return false;
 if(value(p+0x68,mode)&&mode==4)frame=std::floor(frame);
 std::array<float,6> values{};for(uint32_t i=0;i<count;i++){values[i]=curves->curves[i].evaluate(frame);if(!std::isfinite(values[i]))return false;}
 // Only the current interpolation result is changed; native masks and blends follow.
 SIZE_T written{};
 return WriteProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(begin+offset*4),values.data(),count*4,&written)&&written==count*4;
}

void interpolate_detour(void* key,float frame,uint32_t offset,uint32_t count){
 original_interpolate(key,frame,offset,count);
 if(context.motion)trace("interpolation_with_context",context.slot,context.cos,context.motion_id,count);
 try{if(apply(key,frame,offset,count)){
  std::lock_guard<std::mutex> lock(log_mutex);
  if(logged.insert({context.slot,context.cos,context.motion_id}).second)if(auto f=_wfopen((directory+L"quirky-runtime.jsonl").c_str(),L"ab")){
   std::fprintf(f,"{\"event\":\"eye_curves_applied\",\"performer\":%d,\"cos\":%d,\"motion\":%u,\"name\":\"%s\"}\n",context.slot,context.cos,context.motion_id,context.motion->name.c_str());std::fclose(f);
  }
 }
 }catch(...){/* Logging must never interrupt native evaluation. */}
}
void partial_detour(void* partial,void* blocks,const void* indices,int kind){
 trace("partial_hook_called");
 const auto saved=context;context={};
 try{context=select(partial,blocks);}catch(...){context={};}
 original_partial(partial,blocks,indices,kind);context=saved;
}
bool load(){
 auto f=_wfopen((directory+L"quirky-curves.bin").c_str(),L"rb");if(!f)return false;std::vector<unsigned char> bytes;unsigned char buffer[4096];size_t n;
 while((n=std::fread(buffer,1,sizeof(buffer),f))){bytes.insert(bytes.end(),buffer,buffer+n);if(bytes.size()>32*1024*1024){std::fclose(f);return false;}}
 std::fclose(f);try{data=quirky_runtime::parse(bytes);return true;}catch(...){return false;}
}
bool profile(uintptr_t& partial,uintptr_t& interpolate){
 auto base=reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));IMAGE_DOS_HEADER dos{};IMAGE_NT_HEADERS64 nt{};
 if(!value(base,dos)||dos.e_magic!=IMAGE_DOS_SIGNATURE||dos.e_lfanew<=0||dos.e_lfanew>0x100000||!value(base+dos.e_lfanew,nt)||nt.Signature!=IMAGE_NT_SIGNATURE||nt.FileHeader.Machine!=IMAGE_FILE_MACHINE_AMD64||nt.OptionalHeader.SizeOfImage<0x178E300)return false;
 auto match=[&](uintptr_t rva,const unsigned char* expected,size_t length){unsigned char bytes[32]{};return length<=32&&read(base+rva,bytes,length)&&!std::memcmp(bytes,expected,length);};
 if(!match(0x446BC0,quirky_profile::partial,sizeof(quirky_profile::partial))||!match(0x444680,quirky_profile::interpolate,sizeof(quirky_profile::interpolate))||!match(0x4EF3C0,quirky_profile::actor,sizeof(quirky_profile::actor)))return false;
 partial=base+0x446BC0;interpolate=base+0x444680;actor_array=base+0x175D750;return true;
}
}
using namespace quirky_effect;
#ifndef QUIRKY_RUNTIME_TEST
#ifdef XROM_UNIFIED
void StartQuirky(HMODULE module){own_module=module;
#else
extern "C" __declspec(dllexport) void Init(){
#endif
 wchar_t path[32768]{};DWORD n=GetModuleFileNameW(own_module,path,32768);if(!n||n>=32768)return;directory=std::wstring(path,n);auto slash=directory.find_last_of(L"\\/");if(slash==std::wstring::npos)return;directory.resize(slash+1);
 enabled=GetPrivateProfileIntW(L"QuirkyEyes",L"enabled",1,(directory+L"quirky-eyes.ini").c_str())!=0;if(!enabled){event("toggle_disabled");return;}

#ifdef XROM_UNIFIED
 if(!load_policy()){event("invalid_quirky_policy_disabled");return;}
#endif
 uintptr_t partial{},interpolate{};if(!profile(partial,interpolate)){event("unsupported_profile_disabled");return;}if(!load()){event("invalid_curves_disabled");return;}
 auto status=MH_Initialize();if(status!=MH_OK&&status!=MH_ERROR_ALREADY_INITIALIZED){event("hook_init_failed",status);return;}
 status=MH_CreateHook(reinterpret_cast<void*>(partial),reinterpret_cast<void*>(&partial_detour),reinterpret_cast<void**>(&original_partial));
 if(status==MH_OK)status=MH_CreateHook(reinterpret_cast<void*>(interpolate),reinterpret_cast<void*>(&interpolate_detour),reinterpret_cast<void**>(&original_interpolate));
 if(status==MH_OK)status=MH_EnableHook(reinterpret_cast<void*>(interpolate));
 if(status==MH_OK)status=MH_EnableHook(reinterpret_cast<void*>(partial));
 if(status!=MH_OK){MH_DisableHook(reinterpret_cast<void*>(partial));MH_DisableHook(reinterpret_cast<void*>(interpolate));MH_RemoveHook(reinterpret_cast<void*>(partial));MH_RemoveHook(reinterpret_cast<void*>(interpolate));
#ifndef XROM_UNIFIED
MH_Uninitialize();
#endif
event("hooks_failed",status);return;}
 event("quirky_hooks_enabled",static_cast<int>(data.size()));
}
#ifndef XROM_UNIFIED
BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){own_module=module;DisableThreadLibraryCalls(module);}return TRUE;}
#endif
#endif
