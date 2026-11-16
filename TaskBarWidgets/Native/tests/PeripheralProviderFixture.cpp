// Synthetic pipe peer for the managed acquisition owner. No device APIs.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include "../Builtins/utilities/yyjson/yyjson.h"

int main()
{
    wchar_t executable[32768]{};
    GetModuleFileNameW(nullptr, executable, 32768);
    const auto directory = std::filesystem::path(executable).parent_path();
    const bool managedFixture = std::filesystem::path(executable).filename() == L"TaskBarWidgets.PeripheralHost.exe";
    { std::ofstream pid(directory / "fixture.pid"); pid << GetCurrentProcessId(); }
    bool discovered = false;
    const std::string child = "fixture:receiver|unit=12345678|model=000011112222";
    const std::string dockChild = "fixture:dock|unit=pid=00e3;serial=4142434431323334";
    std::string line;
    while (std::getline(std::cin, line))
    {
        { std::ofstream log(directory / "requests.jsonl", std::ios::app); log << line << '\n'; }
        yyjson_doc* doc = yyjson_read(line.data(), line.size(), 0);
        if (!doc) return 2;
        auto root = yyjson_doc_get_root(doc);
        const int request = yyjson_get_int(yyjson_obj_get(root, "requestId"));
        const char* operation = yyjson_get_str(yyjson_obj_get(root, "operation"));
        const std::string op = operation ? operation : "";
        if (managedFixture)
        {
            auto ids = yyjson_obj_get(root, "deviceIds");
            auto queries = yyjson_obj_get(root, "queryIds");
            auto contains = [](yyjson_val* array, const std::string& text) {
                size_t index, count; yyjson_val* value;
                yyjson_arr_foreach(array, index, count, value)
                    if (yyjson_is_str(value) && text == yyjson_get_str(value)) return true;
                return false;
            };
            const bool full = op == "discover" && yyjson_arr_size(ids) == 0;
            std::cout << "{\"version\":1,\"requestId\":" << request << ",\"devices\":[";
            bool first = true;
            for (const std::string unit : {"first", "second"})
            {
                if (!full && (!contains(ids, "vigil:fixture:" + unit) || !contains(queries, "query:" + unit))) continue;
                if (!first) std::cout << ',';
                first = false;
                std::cout << "{\"identity\":\"vigil:fixture:" << unit << "\",\"queryId\":\"query:" << unit
                    << "\",\"name\":\"Synthetic " << unit
                    << R"(","source":"vigil-watt:fixture","kind":"mouse","state":"online","canRead":true,"batteries":[{"role":"main","percentage":42}]})";
            }
            std::cout << "],\"claimedPaths\":[],\"diagnostics\":[],\"error\":\"\"}\n" << std::flush;
            yyjson_doc_free(doc);
            continue;
        }
        std::string mode;
        { std::ifstream input(directory / "mode.txt"); input >> mode; }
        if (op == "read" && mode == "stall")
        {
            std::cout << "{\"version\":1,\"requestId\":" << request
                << ",\"event\":\"initialized\",\"identity\":\"" << child << "\"}\n"
                << "{\"version\":1,\"requestId\":" << request
                << ",\"event\":\"progress\",\"identity\":\"fixture:blocked-interface\"}\n" << std::flush;
            Sleep(INFINITE);
        }
        std::cout << "{\"version\":1,\"requestId\":" << request << ",\"devices\":[";
        if (mode.rfind("dock-", 0) == 0)
        {
            if (op == "discover" && mode != "dock-absent")
                std::cout << R"({"identity":"fixture:dock","name":"Synthetic Razer dock","source":"razer-dock","canRead":true,"batteries":[]})";
            else if ((op == "identify" && mode != "dock-sleep") || op == "read")
            {
                const auto selectedChild = mode == "dock-changed" ?
                    "fixture:dock|unit=pid=00e3;serial=4142434435363738" : dockChild;
                std::cout << "{\"identity\":\"" << selectedChild
                    << R"(","name":"Razer Basilisk V4 Pro","source":"razer-dock","kind":"mouse","state":"online","canRead":true,"batteries":[)";
                if (op == "read") std::cout << R"({"role":"main","percentage":73,"charging":null})";
                std::cout << "]}";
                if (op == "identify")
                    std::cout << R"(,{"identity":"another-dock|unit=pid=00e3;serial=4142434431323334","name":"Foreign dock child","source":"razer-dock","canRead":true},)"
                        << R"({"identity":"fixture:dock|unit=pid=00e3;serial=30303030","name":"Placeholder serial","source":"razer-dock","canRead":true},)"
                        << R"({"identity":"fixture:dock|unit=pid=00e3;serial=554e4b574e313233","name":"Unknown serial","source":"razer-dock","canRead":true},)"
                        << R"({"identity":"fixture:dock|unit=pid=00e3;serial=41424344FF","name":"Non-ASCII serial","source":"razer-dock","canRead":true},)"
                        << R"({"identity":"fixture:dock|unit=pid=ffff;serial=4142434431323334","name":"Unproven model","source":"razer-dock","canRead":true},)"
                        << R"({"identity":"fixture:dock|unit=12345678|model=000011112222","name":"Wrong protocol child","source":"hidpp20","canRead":true},)"
                        << R"({"identity":"fixture:dock","name":"Bare receiver","source":"razer-dock","canRead":true})";
            }
        }
        else if (mode.rfind("rog-", 0) == 0)
        {
            const bool read = op == "read";
            const bool named = read && (mode == "rog-named" || mode == "rog-changed");
            const bool fresh = named || (read && mode == "rog-unknown-model");
            const bool confirmed = read && (fresh || mode == "rog-offline-confirmed" || mode == "rog-offline-unknown" || mode == "rog-invalid-model");
            const char* name = named ? (mode == "rog-named" ? "ROG AZOTH EXTREME" : "ROG AZOTH") :
                read && mode == "rog-offline-confirmed" ? "ROG AZOTH EXTREME SPECIAL EDITION" : "ROG Omni keyboard";
            const char* source = mode == "rog-other-source" ? "another-provider" : "rog_omni_keyboard";
            std::cout << R"({"identity":"hid:v1|serial=41424344|vid=0b05|pid=1ace|mi=2|usage=ff00:0001|channel=rog_omni_keyboard","name":")" << name
                << R"(","source":")" << source << R"(","kind":"keyboard","canRead":true,"state":")"
                << (fresh ? "online" : "unavailable") << R"(","batteries":[)";
            if (fresh) std::cout << R"({"role":"main","percentage":100,"charging":null})";
            std::cout << "]";
            if (confirmed)
            {
                const char* model = mode == "rog-named" ? "6978" : mode == "rog-changed" ? "6789" :
                    mode == "rog-offline-confirmed" ? "7409" : mode == "rog-invalid-model" ? "65535" : "6918";
                std::cout << R"(,"modelIdentityConfirmed":true,"modelIdentity":")" << model << '"';
            }
            std::cout << "}";
        }
        else if (op == "discover")
        {
            discovered = true;
            std::cout << R"({"identity":"fixture:receiver","name":"Synthetic receiver","source":"hidpp20","canRead":true,"batteries":[]})";
        }
        else if (op == "identify" || op == "read")
        {
            if (!discovered) return 3;
            std::cout << "{\"identity\":\"" << child
                << R"(","legacyIdentities":["fixture:legacy-child"],"name":"Synthetic mouse","source":"hidpp20","kind":"mouse","state":"online","canRead":true,"batteries":[)";
            if (op == "read") std::cout << R"({"role":"main","percentage":42,"charging":null})";
            std::cout << "]}";
            if (op == "identify")
                std::cout << R"(,{"identity":"another-receiver|unit=12345678|model=000011112222","name":"Unrelated unit","source":"hidpp20","canRead":true,"batteries":[]})";
        }
        std::cout << "],\"error\":\"\"";
        if (op == "discover" && mode.rfind("rog-", 0) == 0)
            std::cout << R"(,"retiredIdentities":["hid:v1|serial=41424344|vid=0b05|pid=1ace|mi=2|usage=ff01:0001|channel=rog-report3","hid:v1|serial=41424344|vid=0b05|pid=1ad0|mi=2|usage=ff01:0001|channel=rog-report3","fixture:rog-keyboard"] )";
        std::cout << "}\n" << std::flush;
        yyjson_doc_free(doc);
        if (op == "read" && mode == "close-input")
        {
            CloseHandle(GetStdHandle(STD_INPUT_HANDLE));
            { std::ofstream marker(directory / "input-closed"); marker << "closed"; }
            Sleep(INFINITE);
        }
    }
}
