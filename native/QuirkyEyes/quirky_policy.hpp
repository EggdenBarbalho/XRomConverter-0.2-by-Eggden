#pragma once
#include <array>
namespace quirky {
struct Module { int character, module, cos; };
inline constexpr std::array<Module,67> modules = {{
  {0,198,101},
  {0,207,110},
  {0,214,105},
  {0,215,88},
  {0,218,87},
  {1,221,27},
  {1,224,20},
  {1,228,19},
  {2,231,22},
  {2,235,16},
  {2,237,15},
  {5,265,19},
  {3,247,23},
  {4,252,19},
  {4,255,15},
  {1,273,30},
  {0,271,115},
  {3,278,29},
  {0,21,11},
  {0,135,78},
  {0,130,73},
  {0,313,130},
  {0,303,133},
  {0,319,141},
  {0,306,136},
  {1,146,15},
  {2,149,10},
  {2,150,11},
  {3,119,10},
  {4,102,4},
  {4,103,5},
  {4,104,6},
  {0,62,63},
  {0,15,39},
  {1,63,8},
  {2,64,6},
  {3,65,8},
  {3,170,16},
  {4,67,7},
  {4,353,36},
  {4,354,37},
  {5,66,7},
  {5,162,10},
  {0,2004,1004},
  {0,2005,1005},
  {0,2006,1006},
  {0,2013,1013},
  {0,2041,1041},
  {0,2014,1014},
  {0,2023,1023},
  {1,2042,1042},
  {1,2043,1043},
  {1,2029,1029},
  {2,2047,1047},
  {2,2048,1048},
  {2,2032,1032},
  {3,2054,1054},
  {3,2056,1056},
  {4,2037,1037},
  {4,2058,1058},
  {4,2062,1062},
  {5,2038,1038},
  {5,2065,1065},
  {5,2068,1068},
  {0,2070,1070},
  {3,2071,1071},
  {0,284,118},
}};
inline bool matches(bool in_pv, bool enabled, int character, int module, int cos) {
  if (!in_pv || !enabled || character<0 || character>=6 || cos<0) return false;
  for (const auto& entry : modules)
    if (entry.character==character && entry.cos==cos && (module==-1 || module==entry.module)) return true;
  return false;
}
struct PerformerState {
  std::array<bool,6> quirky{};
  bool update(int performer, bool in_pv, bool enabled, int character, int module, int cos) {
    if (performer<0 || performer>=6) return false;
    return quirky[performer]=matches(in_pv,enabled,character,module,cos);
  }
  void reset() { quirky.fill(false); }
};
}
