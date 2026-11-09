#pragma once
namespace quirky {
// MegaMix includes Neru/Haku before Kaito/Meiko; catalog indices do not.
inline constexpr int catalog_character(int runtime_character) {
  switch(runtime_character) {
    case 0: case 1: case 2: case 3: return runtime_character;
    case 6: return 4;
    case 7: return 5;
    default: return -1;
  }
}
}
