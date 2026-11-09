#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include <map>
#include <stdexcept>

namespace quirky_runtime {
struct Key { uint16_t frame; float value,tangent; };
struct Curve {
 bool tangents{};std::vector<Key> keys;
 float evaluate(float frame) const {
  if(keys.empty())return 0;
  if(keys.size()==1||frame<=keys.front().frame)return keys.front().value;
  if(frame>=keys.back().frame)return keys.back().value;
  auto next=std::lower_bound(keys.begin(),keys.end(),frame,[](const Key& k,float f){return k.frame<f;});
  if(next==keys.end())return keys.back().value;
  if(std::fabs(next->frame-frame)<0.000001f)return next->value;
  const auto& previous=*(next-1);float df=frame-previous.frame,t=df/(next->frame-previous.frame);
  float value=(t*2.0f-3.0f)*(t*t)*(previous.value-next->value)+previous.value;
  if(tangents){float tm=t-1.0f;value+=(tm*previous.tangent+t*next->tangent)*tm*df;}
  return value;
 }
};
struct Bone { std::vector<Curve> curves; };
struct Motion { std::string name;std::map<std::string,Bone> bones; };
using Data=std::map<std::pair<int,uint32_t>,Motion>;
inline bool eye(const std::string& s){return s.find("eyelid")!=std::string::npos||s.find("mabu")!=std::string::npos;}
inline Data parse(const std::vector<unsigned char>& bytes){
 size_t offset=0;
 auto get=[&](void* dest,size_t length){if(offset>bytes.size()||length>bytes.size()-offset)throw std::runtime_error("Truncated curves");std::memcpy(dest,bytes.data()+offset,length);offset+=length;};
 auto u8=[&](){uint8_t x;get(&x,1);return x;};
 auto u16=[&](){uint16_t x;get(&x,2);return x;};
 auto u32=[&](){uint32_t x;get(&x,4);return x;};
 auto f32=[&](){float x;get(&x,4);if(!std::isfinite(x))throw std::runtime_error("Nonfinite curve");return x;};
 auto text=[&](){auto n=u16();if(!n||n>127)throw std::runtime_error("Invalid name");std::string x(n,'\0');get(x.data(),n);if(x.find('\0')!=std::string::npos)throw std::runtime_error("Invalid name");return x;};
 char magic[4];get(magic,4);if(std::memcmp(magic,"QEY1",4))throw std::runtime_error("Invalid header");auto count=u32();if(!count||count>2000)throw std::runtime_error("Invalid count");Data data;
 for(uint32_t i=0;i<count;i++){
  int character=static_cast<int>(u32());uint32_t id=u32();if(character<0||character>=6)throw std::runtime_error("Invalid character");Motion motion;motion.name=text();if(motion.name.find("_FACE_")==std::string::npos)throw std::runtime_error("Nonfacial motion");
  auto bones=u32();if(bones!=32)throw std::runtime_error("Invalid eye bone count");
  for(uint32_t j=0;j<bones;j++){auto name=text();if(!eye(name))throw std::runtime_error("Non-eye bone");Bone bone;auto channels=u8();if(channels!=3&&channels!=6)throw std::runtime_error("Invalid channels");
   for(uint8_t c=0;c<channels;c++){Curve curve;auto slope=u8();if(slope>1)throw std::runtime_error("Invalid interpolation");curve.tangents=slope!=0;auto keys=u16();curve.keys.reserve(keys);for(uint16_t k=0;k<keys;k++){Key key;key.frame=u16();key.value=f32();key.tangent=f32();if(k&&curve.keys.back().frame>=key.frame)throw std::runtime_error("Unsorted frames");curve.keys.push_back(key);}bone.curves.push_back(std::move(curve));}
   if(!motion.bones.emplace(name,std::move(bone)).second)throw std::runtime_error("Duplicate bone");
  }
  if(!data.emplace(std::make_pair(character,id),std::move(motion)).second)throw std::runtime_error("Duplicate motion");
 }
 if(offset!=bytes.size())throw std::runtime_error("Trailing curve bytes");
 return data;
}
}
