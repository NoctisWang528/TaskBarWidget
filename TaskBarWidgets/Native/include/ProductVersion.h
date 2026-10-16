#pragma once

#ifdef TBW_USE_GENERATED_VERSION
#include "ProductVersion.generated.h"
#else
// Direct developer builds are visibly unversioned; official packages use build.ps1.
#define TBW_PRODUCT_VERSION_W L"dev"
#define TBW_PRODUCT_VERSION_A "dev"
#define TBW_PRODUCT_VERSION_NUMERIC 0,0,0,0
#endif
