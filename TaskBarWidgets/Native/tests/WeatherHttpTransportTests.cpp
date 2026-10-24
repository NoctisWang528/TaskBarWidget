#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <winhttp.h>
#include "../Builtins/Weather/Common.h"
#include <atomic>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#pragma comment(lib, "ws2_32.lib")

using Common = WeatherCCommon;
using Stage = Common::RequestStage;
static void Check(bool value, const char* message)
{
    if (!value) throw std::runtime_error(message);
}

class Server
{
    SOCKET listener{INVALID_SOCKET};
    std::thread worker;
public:
    unsigned short port{};
    std::atomic<unsigned> requests{};
    Server(std::string response, DWORD delay = 0, bool delayBody = false, bool trickle = false)
    {
        listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        Check(listener != INVALID_SOCKET, "socket");
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if (bind(listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0
            || listen(listener, 1) != 0) {
            closesocket(listener); throw std::runtime_error("bind/listen");
        }
        int length = sizeof(address);
        Check(getsockname(listener, reinterpret_cast<sockaddr*>(&address), &length) == 0, "getsockname");
        port = ntohs(address.sin_port);
        worker = std::thread([this, response, delay, delayBody, trickle]() {
            fd_set pending;
            FD_ZERO(&pending); FD_SET(listener, &pending);
            timeval timeout{15, 0};
            if (select(0, &pending, nullptr, nullptr, &timeout) <= 0) return;
            SOCKET client = accept(listener, nullptr, nullptr);
            if (client == INVALID_SOCKET) return;
            DWORD ioTimeout = 5000;
            setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&ioTimeout), sizeof(ioTimeout));
            setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&ioTimeout), sizeof(ioTimeout));
            std::string request;
            char buffer[1024];
            while (request.find("\r\n\r\n") == std::string::npos && request.size() < 16384) {
                int count = recv(client, buffer, sizeof(buffer), 0);
                if (count <= 0) break;
                request.append(buffer, count);
            }
            ++requests;
            auto sendAll = [client](const std::string& data) {
                size_t offset = 0;
                while (offset < data.size()) {
                    int count = send(client, data.data() + offset, static_cast<int>(data.size() - offset), 0);
                    if (count <= 0) break;
                    offset += count;
                }
            };
            if (delayBody) {
                const auto split = response.find("\r\n\r\n") + 4;
                sendAll(response.substr(0, split));
                if (trickle) {
                    for (size_t index = split; index < response.size(); ++index) {
                        Sleep(delay);
                        sendAll(response.substr(index, 1));
                    }
                } else {
                    Sleep(delay);
                    sendAll(response.substr(split));
                }
            } else {
                Sleep(delay);
                sendAll(response);
            }
            shutdown(client, SD_SEND);
            closesocket(client);
        });
    }
    ~Server()
    {
        if (worker.joinable()) worker.join();
        closesocket(listener);
    }
    bool Get(std::wstring& body, bool redirects = false)
    {
        return Common::GetUrl((L"http://127.0.0.1:" + std::to_wstring(port) + L"/test").c_str(), nullptr, body, redirects);
    }
};

static std::string Response(unsigned status, const std::string& body, const std::string& extra = "")
{
    return "HTTP/1.1 " + std::to_string(status) + " Test\r\nContent-Length: "
        + std::to_string(body.size()) + "\r\nConnection: close\r\n" + extra + "\r\n" + body;
}

static bool Synthetic(const wchar_t*, const wchar_t*, std::wstring&, bool)
{
    Check(Common::LastHttpStatus() == 0 && Common::LastTransportError() == 0
        && Common::LastRequestStage() == Stage::None, "reset before synthetic");
    Common::SetTestResponse(200, ERROR_WINHTTP_CONNECTION_ERROR, Stage::Read);
    return false;
}

int main()
{
    WSADATA data{};
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0) return 2;
    try {
        std::wstring body;
        {
            Server server(Response(200, "{}"));
            Check(server.Get(body) && body == L"{}", "plain 200");
            Check(Common::LastHttpStatus() == 200 && !Common::LastTransportError(), "plain diagnostics");
        }
        {
            const unsigned char gzip[] = {0x1f,0x8b,8,0,0,0,0,0,0,3,1,2,0,0xfd,0xff,0x7b,0x7d,0x43,0xbf,0xa6,0xa3,2,0,0,0};
            Server server(Response(200, std::string(reinterpret_cast<const char*>(gzip), sizeof(gzip)), "Content-Encoding: gzip\r\n"));
            Check(server.Get(body) && body == L"{}", "gzip decoding");
        }
        {
            const unsigned char gzip[] = {0x1f,0x8b,8,0,0,0,0,0,0,3,1,2,0,0xfd,0xff,0x7b,0x7d,0x43,0xbf,0xa6,0xa3,2,0,0,0};
            Server server(Response(200, std::string(reinterpret_cast<const char*>(gzip), sizeof(gzip)), "Content-Encoding: gzip\r\n"), 200, true);
            HINTERNET session = WinHttpOpen(L"WeatherHttpHeaderTest", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, nullptr, nullptr, 0);
            HINTERNET connection = session ? WinHttpConnect(session, L"127.0.0.1", server.port, 0) : nullptr;
            HINTERNET request = connection ? WinHttpOpenRequest(connection, L"GET", L"/", nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, 0) : nullptr;
            DWORD decompression = WINHTTP_DECOMPRESSION_FLAG_GZIP | WINHTTP_DECOMPRESSION_FLAG_DEFLATE;
            bool good = request && WinHttpSetTimeouts(request, 3000, 3000, 3000, 3000)
                && WinHttpSetOption(request, WINHTTP_OPTION_DECOMPRESSION, &decompression, sizeof(decompression))
                && WinHttpSendRequest(request, nullptr, 0, nullptr, 0, 0, 0) && WinHttpReceiveResponse(request, nullptr);
            DWORD length{}, size = sizeof(length);
            bool hasLength = good && WinHttpQueryHeaders(request, WINHTTP_QUERY_CONTENT_LENGTH | WINHTTP_QUERY_FLAG_NUMBER,
                WINHTTP_HEADER_NAME_BY_INDEX, &length, &size, nullptr);
            wchar_t encoding[64]{};
            size = sizeof(encoding);
            bool hasEncoding = good && WinHttpQueryHeaders(request, WINHTTP_QUERY_CONTENT_ENCODING,
                WINHTTP_HEADER_NAME_BY_INDEX, encoding, &size, nullptr);
            if (request) WinHttpCloseHandle(request);
            if (connection) WinHttpCloseHandle(connection);
            if (session) WinHttpCloseHandle(session);
            std::cout << "gzip headers: ready=" << good << ", Content-Length present=" << hasLength
                << ", value=" << length << ", Content-Encoding present=" << hasEncoding << "\n";
            Check(good && !hasLength && !hasEncoding, "gzip decoded headers remove wire length and encoding");
        }
        for (unsigned status : {401u, 403u}) {
            Server server(Response(status, "{\"error\":\"denied\"}"));
            Check(!server.Get(body) && body == L"{\"error\":\"denied\"}", "error JSON");
            Check(Common::LastHttpStatus() == status && !Common::LastTransportError(), "HTTP error diagnostics");
        }
        for (unsigned status : {200u, 418u}) {
            Server server("HTTP/1.1 " + std::to_string(status) + " Test\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{}");
            Check(!server.Get(body), "short error/success body");
            Check(Common::LastHttpStatus() == status && Common::LastTransportError() != 0
                && Common::LastRequestStage() == Stage::Read, "short body diagnostics");
            Check(Common::LastTransportErrorMessage().find(L"读取响应体") != std::wstring::npos, "short body message stage");
        }
        for (unsigned removed : {0u, 5u}) {
            const unsigned char gzip[] = {0x1f,0x8b,8,0,0,0,0,0,0,3,1,2,0,0xfd,0xff,0x7b,0x7d,0x43,0xbf,0xa6,0xa3,2,0,0,0};
            std::string compressed(reinterpret_cast<const char*>(gzip), sizeof(gzip) - removed);
            Server server("HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 100\r\nConnection: close\r\n\r\n" + compressed, 200, true);
            Check(!server.Get(body), "gzip short Content-Length");
            Check(Common::LastHttpStatus() == 200 && Common::LastTransportError() != 0
                && Common::LastRequestStage() == Stage::Read && body.empty(), "gzip short diagnostics");
        }
        {
            Server server(Response(302, "redirect", "Location: /must-not-follow\r\n"));
            Check(!server.Get(body) && body == L"redirect", "redirect disabled");
            Check(Common::LastHttpStatus() == 302 && !Common::LastTransportError() && server.requests == 1, "redirect status");
        }
        {
            Server server("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{}");
            Check(!server.Get(body) && body.empty(), "truncated 200 must fail");
            SetLastError(ERROR_ACCESS_DENIED);
            Check(Common::LastHttpStatus() == 200 && Common::LastTransportError() != 0
                && Common::LastRequestStage() == Stage::Read, "read error survives handle close");
            Check(Common::LastTransportErrorMessage().find(L"读取") != std::wstring::npos, "read message");
        }
        {
            Server server(Response(200, std::string(13, 'x')), 900, true, true);
            const auto started = GetTickCount64();
            Check(!server.Get(body), "body deadline must fail despite incoming bytes");
            const auto elapsed = GetTickCount64() - started;
            Check(elapsed >= 9000 && elapsed < 14000, "bounded body deadline");
            Check(Common::LastHttpStatus() == 200 && Common::LastRequestStage() == Stage::Read
                && (Common::LastTransportError() == ERROR_TIMEOUT || Common::LastTransportError() == ERROR_WINHTTP_TIMEOUT), "deadline diagnostics");
        }
        for (bool delayBody : {false, true}) {
            Server server(Response(200, "{}"), 15000, delayBody);
            const bool good = server.Get(body);
            Check(!good, delayBody ? "body timeout must fail" : "header timeout must fail");
            Check(Common::LastTransportError() == ERROR_WINHTTP_TIMEOUT || Common::LastTransportError() == ERROR_TIMEOUT, "timeout error");
            Check(Common::LastRequestStage() == (delayBody ? Stage::Read : Stage::Receive), "timeout stage");
            Check(Common::LastHttpStatus() == (delayBody ? 200u : 0u), "timeout status");
            Check(Common::LastTransportErrorMessage().find(L"超时") != std::wstring::npos, "timeout message");
        }
        {
            SOCKET reserved = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
            Check(reserved != INVALID_SOCKET, "refusal socket");
            sockaddr_in address{};
            address.sin_family = AF_INET;
            address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
            Check(bind(reserved, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0, "refusal bind");
            int size = sizeof(address);
            Check(getsockname(reserved, reinterpret_cast<sockaddr*>(&address), &size) == 0, "refusal port");
            const bool good = Common::GetUrl((L"http://127.0.0.1:" + std::to_wstring(ntohs(address.sin_port)) + L"/").c_str(), nullptr, body, false);
            closesocket(reserved);
            Check(!good && Common::LastHttpStatus() == 0 && Common::LastTransportError() == ERROR_WINHTTP_CANNOT_CONNECT, "connection refusal");
            Check(Common::LastRequestStage() == Stage::Send, "refusal stage");
        }
        for (unsigned status : {403u, 418u}) {
            for (size_t size : {size_t(8192), size_t(8193), size_t(65536)}) {
                Server server(Response(status, std::string(size, 'x')));
                Check(!server.Get(body) && body.size() == (size > 8192 ? 8192 : size), "error body cap");
                Check(Common::LastHttpStatus() == status, "capped HTTP status");
                Check(Common::LastTransportError() == (size > 8192 ? ERROR_INSUFFICIENT_BUFFER : 0), "cap error");
                if (size > 8192) Check(Common::LastRequestStage() == Stage::Read, "cap stage");
            }
        }
        for (size_t size : {size_t(1024 * 1024), size_t(1024 * 1024 + 1)}) {
            Server server(Response(200, std::string(size, 'x')));
            Check(server.Get(body) == (size == 1024 * 1024), "success body cap");
            Check(body.size() == (size == 1024 * 1024 ? size : 0), "incomplete success hidden");
        }
        Common::SetTestTransport(Synthetic);
        Check(!Common::GetUrl(L"http://unused", nullptr, body), "synthetic result");
        Check(Common::LastHttpStatus() == 200 && Common::LastRequestStage() == Stage::Read, "synthetic diagnostics");
        Common::SetTestTransport(nullptr);
        const std::vector<std::pair<DWORD, std::wstring>> messages = {
            {12007, L"DNS"}, {12029, L"连接"}, {12002, L"超时"}, {ERROR_TIMEOUT, L"超时"}, {12175, L"TLS"}
        };
        for (const auto& item : messages) {
            Common::SetTestResponse(0, item.first, Stage::Send);
            Check(Common::LastTransportErrorMessage().find(item.second) != std::wstring::npos, "safe mapped message");
        }
        Check(!Common::GetUrl(L"invalid", nullptr, body), "invalid URL");
        Check(Common::LastHttpStatus() == 0 && Common::LastRequestStage() == Stage::Url, "URL reset");
        Common::SetTestResponse(403, 0);
        Check(Common::LastTransportErrorMessage().empty(), "HTTP-only message empty");
        std::cout << "PASS: WinHTTP loopback transport cases and diagnostic contract\n";
    } catch (const std::exception& error) {
        std::cerr << "FAIL: " << error.what() << " (HTTP=" << Common::LastHttpStatus()
            << ", error=" << Common::LastTransportError() << ", stage=" << static_cast<int>(Common::LastRequestStage()) << ")\n";
        WSACleanup(); return 1;
    }
    WSACleanup();
    return 0;
}
