#define QUIRKY_RUNTIME_TEST
#include "runtime.cpp"
#include <cassert>
#include <iostream>
#include <fstream>
#include <iterator>

int calls{};
void native_interpolate(void* key,float,uint32_t offset,uint32_t count){
 calls++;uintptr_t begin{};assert(value(reinterpret_cast<uintptr_t>(key)+0x38,begin));
 for(uint32_t i=0;i<count;i++)reinterpret_cast<float*>(begin)[offset+i]=77.0f;
}
void native_partial(void* partial,void* blocks,const void*,int){
 // Mimic the native sequence: interpolation happens inside the partial evaluation.
 interpolate_detour(static_cast<unsigned char*>(partial)+0x38,0.0f,4,3);
 // Mouth/non-eye offsets must retain the native result.
 interpolate_detour(static_cast<unsigned char*>(partial)+0x38,0.0f,12,3);
 assert(blocks);
}
void hooked_partial(void* partial,void* blocks,const void*,int){
 Interpolate volatile invoke=&native_interpolate;
 invoke(static_cast<unsigned char*>(partial)+0x38,0.0f,4,3);
 invoke(static_cast<unsigned char*>(partial)+0x38,0.0f,12,3);
 assert(blocks);
}
int main(int argc,char** argv){
 assert(argc==2);std::string path=argv[1];directory.assign(path.begin(),path.end());directory+=L"/";assert(load());assert(data.size()==618);
 // Malformed distributable data must fail before hooks are enabled.
 std::ifstream input(path+"/quirky-curves.bin",std::ios::binary);
 std::vector<unsigned char> payload((std::istreambuf_iterator<char>(input)),{});
 auto reject=[](const std::vector<unsigned char>& bytes){bool rejected=false;try{quirky_runtime::parse(bytes);}catch(...){rejected=true;}assert(rejected);};
 auto broken=payload;broken[0]='!';reject(broken);
 broken=payload;broken.pop_back();reject(broken);
 broken=payload;broken.push_back(0);reject(broken);
 broken=payload;int invalid_character=6;std::memcpy(broken.data()+8,&invalid_character,4);reject(broken);
 reject({});
 std::cout<<"PASS: invalid magic, truncation, trailing bytes, invalid character and empty data rejected.\n";
 using quirky_runtime::Curve;
 Curve curve;curve.keys={{0,0,0},{10,1,0}};assert(curve.evaluate(-1)==0&&curve.evaluate(15)==1&&curve.evaluate(5)==0.5f);
 curve.tangents=true;curve.keys[0].tangent=2;curve.keys[1].tangent=-1;assert(curve.evaluate(5)==4.25f);
 for(const auto& record:data)for(const auto& bone:record.second.bones){assert(quirky_runtime::eye(bone.first));for(const auto& c:bone.second.curves){assert(std::isfinite(c.evaluate(0)));if(!c.keys.empty())for(const auto& k:c.keys)assert(std::fabs(c.evaluate(k.frame)-k.value)<0.000001f);}}
 std::vector<unsigned char> actors(6*0x8118),bone_data(0x1000),blocks(2*0x2F0),node(0x50),other_node(0x50);actor_array=reinterpret_cast<uintptr_t>(actors.data());
 auto put=[](unsigned char* p,const auto& v){std::memcpy(p,&v,sizeof(v));};
 for(int i=0;i<6;i++){int8_t id=static_cast<int8_t>(i);put(actors.data()+i*0x8118,id);}
 int native=0,cos=118;put(actors.data()+0x10,native);put(actors.data()+0x14,cos);uintptr_t bones=reinterpret_cast<uintptr_t>(bone_data.data());put(actors.data()+0x28,bones);
 auto record=std::find_if(data.begin(),data.end(),[](const auto& x){return x.first.first==0;});assert(record!=data.end());
 auto bone=std::find_if(record->second.bones.begin(),record->second.bones.end(),[](const auto& x){return x.second.curves.size()==3;});assert(bone!=record->second.bones.end());
 std::array<char,128> name{};std::memcpy(name.data(),bone->first.data(),bone->first.size());uintptr_t nameptr=reinterpret_cast<uintptr_t>(name.data()),nodeptr=reinterpret_cast<uintptr_t>(node.data());put(node.data(),nameptr);put(blocks.data()+0x2C8,nodeptr);
 uint32_t offset=4,count=3;put(blocks.data()+0x18,offset);put(blocks.data()+0x1C,count);
 std::array<char,128> other_name{};std::strcpy(other_name.data(),"kl_ago_wj");nameptr=reinterpret_cast<uintptr_t>(other_name.data());nodeptr=reinterpret_cast<uintptr_t>(other_node.data());put(other_node.data(),nameptr);put(blocks.data()+0x2F0+0x2C8,nodeptr);offset=12;put(blocks.data()+0x2F0+0x18,offset);put(blocks.data()+0x2F0+0x1C,count);
 std::array<uintptr_t,3> vector{reinterpret_cast<uintptr_t>(blocks.data()),reinterpret_cast<uintptr_t>(blocks.data()+blocks.size()),reinterpret_cast<uintptr_t>(blocks.data()+blocks.size())};
 std::array<float,32> values{};
 auto prepare=[&](size_t layer){put(bone_data.data()+layer+0x98,record->first.second);uintptr_t begin=reinterpret_cast<uintptr_t>(values.data()),end=begin+sizeof(values);put(bone_data.data()+layer+0x38+0x38,begin);put(bone_data.data()+layer+0x38+0x40,end);};
 original_interpolate=&native_interpolate;original_partial=&native_partial;
 for(size_t layer:{size_t(0xC8),size_t(0x640)}){
  prepare(layer);values.fill(-99);partial_detour(bone_data.data()+layer,vector.data(),nullptr,0);
  for(int i=0;i<3;i++){assert(values[4+i]==bone->second.curves[i].evaluate(0));assert(values[12+i]==77);}
  assert(values[0]==-99);assert(context.motion==nullptr);
 }
 // Normal costume, extra character, disabled toggle and nonfacial layer retain native values.
 for(int scenario=0;scenario<4;scenario++){
  native=scenario==1?4:0;cos=scenario==0?0:118;enabled=scenario!=2;put(actors.data()+0x10,native);put(actors.data()+0x14,cos);
  size_t layer=scenario==3?0x410:0xC8;prepare(layer);values.fill(-99);partial_detour(bone_data.data()+layer,vector.data(),nullptr,0);assert(values[4]==77&&values[12]==77);
 }
 // A second actor's costume does not inherit the first actor's selection.
 native=0;cos=118;enabled=true;put(actors.data()+0x10,native);put(actors.data()+0x14,cos);
 std::vector<unsigned char> second(0x1000);bones=reinterpret_cast<uintptr_t>(second.data());put(actors.data()+0x8118+0x28,bones);cos=0;put(actors.data()+0x8118+0x10,native);put(actors.data()+0x8118+0x14,cos);
 assert(!select(second.data()+0xC8,vector.data()).motion);assert(select(bone_data.data()+0xC8,vector.data()).motion);
 assert(!select(nullptr,vector.data()).motion);assert(calls==12);
 // Shipping game blocks have no readable name: use validated bone_data block IDs.
 std::vector<unsigned char> anonymous(quirky_blocks::size(0)*0x2F0);
 auto native_table=quirky_blocks::table(0);unsigned eye_id=0;
 for(unsigned i=0;i<quirky_blocks::size(0);i++)if(bone->first==native_table[i].name)eye_id=i;
 for(unsigned i=0;i<quirky_blocks::size(0);i++){put(anonymous.data()+i*0x2F0+8,i);put(anonymous.data()+i*0x2F0+0x1C,native_table[i].count);}
 uintptr_t anonymous_node=reinterpret_cast<uintptr_t>(other_node.data());uintptr_t null_name=0;put(other_node.data(),null_name);
 put(anonymous.data()+eye_id*0x2F0+0x2C8,anonymous_node);unsigned anon_offset=4;put(anonymous.data()+eye_id*0x2F0+0x18,anon_offset);
 uintptr_t anon_begin=reinterpret_cast<uintptr_t>(anonymous.data()),anon_end=anon_begin+anonymous.size();auto saved_vector=vector;vector={anon_begin,anon_end,anon_end};
 prepare(0xC8);values.fill(-99);partial_detour(bone_data.data()+0xC8,vector.data(),nullptr,0);assert(values[4]==bone->second.curves[0].evaluate(0)&&values[12]==77);
 vector=saved_vector;
 std::cout<<"PASS: 185 anonymous native blocks resolve eyelid curves through bone_data IDs.\n";
 // Exercise real anonymous-block selection for all six native character IDs.
 auto saved_record=record;auto saved_bone=bone;
 int native_ids[]={0,1,2,3,6,7};
 for(int ch=0;ch<6;ch++){
  native=native_ids[ch];cos=-1;for(const auto& module:quirky::modules)if(module.character==ch){cos=module.cos;break;}assert(cos>=0);
  put(actors.data()+0x10,native);put(actors.data()+0x14,cos);
  record=std::find_if(data.begin(),data.end(),[&](const auto& x){return x.first.first==ch;});assert(record!=data.end());
  bone=std::find_if(record->second.bones.begin(),record->second.bones.end(),[](const auto& x){return x.second.curves.size()==3;});assert(bone!=record->second.bones.end());
  std::vector<unsigned char> native_blocks(quirky_blocks::size(ch)*0x2F0);auto table=quirky_blocks::table(ch);unsigned selected_id=0;
  for(unsigned i=0;i<quirky_blocks::size(ch);i++){put(native_blocks.data()+i*0x2F0+8,i);put(native_blocks.data()+i*0x2F0+0x1C,table[i].count);if(bone->first==table[i].name)selected_id=i;}
  put(native_blocks.data()+selected_id*0x2F0+0x2C8,anonymous_node);put(native_blocks.data()+selected_id*0x2F0+0x18,anon_offset);
  uintptr_t start=reinterpret_cast<uintptr_t>(native_blocks.data());vector={start,start+native_blocks.size(),start+native_blocks.size()};caches.clear();prepare(0xC8);values.fill(-99);
  partial_detour(bone_data.data()+0xC8,vector.data(),nullptr,0);assert(values[4]==bone->second.curves[0].evaluate(0)&&values[12]==77);
  cos=0;put(actors.data()+0x14,cos);values.fill(-99);partial_detour(bone_data.data()+0xC8,vector.data(),nullptr,0);assert(values[4]==77);
 }
 record=saved_record;bone=saved_bone;native=0;cos=118;put(actors.data()+0x10,native);put(actors.data()+0x14,cos);vector=saved_vector;caches.clear();
 std::cout<<"PASS: Miku, Rin, Len, Luka, KAITO and MEIKO anonymous-block application; each normal COS remains native.\n";
 // Real MinHook trampoline/ABI roundtrip through both nested detours.
 assert(MH_Initialize()==MH_OK);
 assert(MH_CreateHook(reinterpret_cast<void*>(&hooked_partial),reinterpret_cast<void*>(&partial_detour),reinterpret_cast<void**>(&original_partial))==MH_OK);
 assert(MH_CreateHook(reinterpret_cast<void*>(&native_interpolate),reinterpret_cast<void*>(&interpolate_detour),reinterpret_cast<void**>(&original_interpolate))==MH_OK);
 assert(MH_EnableHook(MH_ALL_HOOKS)==MH_OK);
 prepare(0xC8);values.fill(-99);Partial volatile invoke=&hooked_partial;invoke(bone_data.data()+0xC8,vector.data(),nullptr,0);
 assert(values[4]==bone->second.curves[0].evaluate(0)&&values[12]==77);assert(context.motion==nullptr);
 assert(MH_DisableHook(MH_ALL_HOOKS)==MH_OK);values.fill(-99);invoke(bone_data.data()+0xC8,vector.data(),nullptr,0);assert(values[4]==77);
 assert(MH_Uninitialize()==MH_OK);
 std::cout<<"PASS: 618 motion curves, exact key values, native Hermite, face/eyelid application, other channels, normal/extra/disabled/mouth fallback, two-actor isolation and context reset.\n";
 std::cout<<"PASS: nested MinHook trampolines preserve Win64 floating-point/integer ABI; removal restores native interpolation.\n";
}
