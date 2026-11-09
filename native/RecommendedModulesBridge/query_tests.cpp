#include "recommended_query.hpp"
#include <cassert>
#include <cstdio>
int main(){using namespace xrom_recommended;
for(const auto& e:entries){assert(resolve(true,e.pv,e.performer,e.runtime_character,77)==e.cos);assert(resolve(false,e.pv,e.performer,e.runtime_character,77)==77);assert(resolve(true,e.pv,e.performer,9,77)==77);assert(resolve(true,e.pv,-1,e.runtime_character,77)==77);assert(resolve(true,e.pv,6,e.runtime_character,77)==77);}
for(int pv=0;pv<800;pv++)for(int slot=0;slot<6;slot++)assert(resolve(true,pv,slot,0,77)==77);
assert(resolve(true,9999,0,0,77)==77);
assert(resolve(true,801,0,0,0)==1002); // Silent Voice COS, not module ID or COS+1.
std::puts("PASS: 40 recommendation queries; disabled/unmapped/extra characters preserve original COS; existing PV IDs only.");}
