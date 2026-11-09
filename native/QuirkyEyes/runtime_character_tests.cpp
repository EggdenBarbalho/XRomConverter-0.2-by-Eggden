#include "quirky_policy.hpp"
#include "runtime_character.hpp"
#include <cassert>
#include <cstdio>
int main(){
 const int runtime[]={0,1,2,3,6,7};
 for(const auto& entry:quirky::modules){
  const int mapped=quirky::catalog_character(runtime[entry.character]);
  assert(mapped==entry.character);
  assert(quirky::matches(true,true,mapped,-1,entry.cos));
 }
 for(int extra:{-1,4,5,8,9,10}){
  assert(quirky::catalog_character(extra)==-1);
  for(const auto& entry:quirky::modules)assert(!quirky::matches(true,true,quirky::catalog_character(extra),-1,entry.cos));
 }
 assert(quirky::matches(true,true,quirky::catalog_character(0),-1,118));
 assert(!quirky::matches(true,true,quirky::catalog_character(0),-1,0));
 std::puts("PASS: native character mapping, all 67 Quirky entries and exclusion of extra characters.");
}
