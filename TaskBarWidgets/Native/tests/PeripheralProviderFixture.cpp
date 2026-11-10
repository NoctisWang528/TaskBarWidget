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
    { std::ofstream pid(directory / "fixture.pid"); pid << GetCurrentProcessId(); }
    bool discovered = false;
    const std::string child = "fixture:receiver|unit=12345678|model=000011112222";
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
        if (op == "discover")
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
        std::cout << "],\"error\":\"\"}\n" << std::flush;
        yyjson_doc_free(doc);
        if (op == "read" && mode == "close-input")
        {
            CloseHandle(GetStdHandle(STD_INPUT_HANDLE));
            { std::ofstream marker(directory / "input-closed"); marker << "closed"; }
            Sleep(INFINITE);
        }
    }
}
