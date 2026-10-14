#include "../TrafficMonitor/MediaDisplayState.h"
#include "BuiltinIniOptionsTests.h"
#include <iostream>

int main()
{
    TestMediaDisplayState();
    TestBuiltinIniOptions();
    std::cout << "Native mode-state regression assertions passed.\n";
    return 0;
}
