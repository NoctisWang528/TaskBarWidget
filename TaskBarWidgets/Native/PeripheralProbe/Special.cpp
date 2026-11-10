#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2bth.h>
#include <windows.h>
#include <objbase.h>
#include <bluetoothapis.h>
#include <setupapi.h>
#include <devpkey.h>
#include <bcrypt.h>
#include <algorithm>
#include <cwctype>
#include <map>
#include <numeric>
#include "Special.h"

namespace peripheral
{
namespace
{
std::string Text(const wchar_t* input)
{
    int n = WideCharToMultiByte(CP_UTF8,0,input,-1,nullptr,0,nullptr,nullptr);
    if (n<=1) return {}; std::string result(n,0);
    WideCharToMultiByte(CP_UTF8,0,input,-1,result.data(),n,nullptr,nullptr);result.pop_back();return result;
}
std::wstring Wide(const std::string& input)
{
    int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,input.data(),int(input.size()),nullptr,0);
    std::wstring result(n,0);if(n)MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,input.data(),int(input.size()),result.data(),n);return result;
}
std::string Lower(std::string s) { for(auto& ch:s)if(ch>='A'&&ch<='Z')ch+='a'-'A';return s; }
struct Handle
{
    HANDLE h=INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE value):h(value){}
    ~Handle(){if(h!=INVALID_HANDLE_VALUE&&h)CloseHandle(h);}
};
std::string Container(HDEVINFO list,SP_DEVINFO_DATA& info)
{
    GUID id{};DEVPROPTYPE type=0;DWORD size=0;wchar_t text[40]{};
    if(!SetupDiGetDevicePropertyW(list,&info,&DEVPKEY_Device_ContainerId,&type,reinterpret_cast<PBYTE>(&id),sizeof(id),&size,0)||
        type!=DEVPROP_TYPE_GUID||IsEqualGUID(id,GUID_NULL)||!StringFromGUID2(id,text,40))return {};
    return Lower(Text(text));
}
struct Interface{std::string path,container,instance;unsigned vid=0,pid=0;int mi=-1;std::string port;};
std::vector<Interface> Interfaces(const GUID& guid, const std::set<std::string>& excluded = {}, const std::function<void(const std::string&)>& progress = {})
{
    std::vector<Interface> result;
    HDEVINFO list=SetupDiGetClassDevsW(&guid,nullptr,nullptr,DIGCF_DEVICEINTERFACE|DIGCF_PRESENT);
    if(list==INVALID_HANDLE_VALUE)return result;
    for(DWORD index=0;index<4096;++index)
    {
        SP_DEVICE_INTERFACE_DATA iface{};iface.cbSize=sizeof(iface);
        if(!SetupDiEnumDeviceInterfaces(list,nullptr,&guid,index,&iface))break;
        DWORD size=0;SetupDiGetDeviceInterfaceDetailW(list,&iface,nullptr,0,&size,nullptr);
        if(size<sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)||size>32768)continue;
        std::vector<unsigned char> data(size);auto detail=reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(data.data());detail->cbSize=sizeof(*detail);
        SP_DEVINFO_DATA info{};info.cbSize=sizeof(info);
        if(!SetupDiGetDeviceInterfaceDetailW(list,&iface,detail,size,nullptr,&info))continue;
        wchar_t instance[2048]{};
        if(!SetupDiGetDeviceInstanceIdW(list,&info,instance,2048,nullptr))continue;
        Interface entry;entry.path=Lower(Text(detail->DevicePath));entry.instance=Lower(Text(instance));
        if(excluded.count(entry.path)||excluded.count("pnp-path:v1|"+entry.path)||excluded.count(entry.path+"\\xmosctl"))continue;
        if(progress)progress("pnp-path:v1|"+entry.path);
        entry.container=Container(list,info);
        unsigned mi=0;
        if(entry.instance.rfind("usb\\vid_",0)!=0||sscanf_s(entry.instance.c_str()+8,"%4x&pid_%4x&mi_%2x",&entry.vid,&entry.pid,&mi)!=3||entry.container.empty())continue;
        entry.mi=int(mi);
        HKEY key=SetupDiOpenDevRegKey(list,&info,DICS_FLAG_GLOBAL,0,DIREG_DEV,KEY_READ);
        if(key!=INVALID_HANDLE_VALUE)
        {
            wchar_t port[32]{};DWORD length=sizeof(port),type=0;
            if(RegQueryValueExW(key,L"PortName",nullptr,&type,reinterpret_cast<BYTE*>(port),&length)==ERROR_SUCCESS&&type==REG_SZ)entry.port=Text(port);
            RegCloseKey(key);
        }
        result.push_back(std::move(entry));
    }
    SetupDiDestroyDeviceInfoList(list);return result;
}
constexpr GUID AudioGuid={0x6994ad04,0x93ef,0x11d0,{0xa3,0xcc,0x00,0xa0,0xc9,0x22,0x31,0x96}};
constexpr GUID ControlGuid={0x50894fa4,0x9206,0x4a91,{0x87,0x28,0xcf,0x6f,0x26,0x8f,0x27,0x1d}};
constexpr GUID ComGuid={0x86e0d1e0,0x8089,0x11d0,{0x9c,0xe4,0x08,0x00,0x3e,0x30,0x1f,0x73}};
constexpr GUID GaiaGuid={0x54943f52,0xbd0d,0x48a0,{0x85,0x5a,0x3b,0x54,0x71,0xa6,0xb4,0xab}};
bool OHubId(unsigned vid,unsigned pid)
{
    return (vid==0x39a5&&(pid==0x3d21||pid==0x3ff1))||(vid==0x3a95&&pid==0x3ee3)||(vid==0x3206&&pid==0x7015);
}
std::optional<Reading> DriverRead(const Candidate& c, const std::function<void()>& progress)
{
    bool present=false;
    for(const auto& live:DiscoverSpecial())if(live.identity==c.identity&&live.protocol==Protocol::OHub)present=true;
    if(!present)return {};
    auto path=Wide(c.path);Handle h(CreateFileW(path.c_str(),GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,0,nullptr));
    if(h.h==INVALID_HANDLE_VALUE)return {};
    auto q=BatteryQuery(c);std::optional<Reading> result;
    for(unsigned round=0;round<2;++round)
    {
        if(progress)progress();
        Bytes reply(128,0);DWORD count=0;
        if(!DeviceIoControl(h.h,2233555,q.packet.data(),DWORD(q.packet.size()),reply.data(),DWORD(reply.size()),&count,nullptr)||count>128)return {};
        reply.resize(count);result=Decode(c,reply);if(!result)return {};
    }
    return result;
}
std::optional<Reading> SerialRead(const Candidate& c, const std::function<void()>& progress)
{
    Candidate current=c;current.transportPath.clear();AttachSerialTransport(current);
    if(current.transportPath.empty()||current.transportPath!=c.transportPath)return {};
    auto path=Wide(c.transportPath);Handle h(CreateFileW(path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr));
    if(h.h==INVALID_HANDLE_VALUE)return {};
    DCB original{};original.DCBlength=sizeof(original);DCB config{};config.DCBlength=sizeof(config);
    COMMTIMEOUTS timeouts{},originalTimeouts{};
    if(!GetCommState(h.h,&original)||!GetCommTimeouts(h.h,&originalTimeouts))return {};
    struct Restore{HANDLE h;DCB dcb;COMMTIMEOUTS timeouts;~Restore(){SetCommState(h,&dcb);SetCommTimeouts(h,&timeouts);}}restore{h.h,original,originalTimeouts};
    config=original;config.BaudRate=c.protocol==Protocol::SonyH9?460800:9600;config.ByteSize=8;config.Parity=NOPARITY;config.StopBits=ONESTOPBIT;
    config.fBinary=TRUE;config.fParity=FALSE;config.fOutxCtsFlow=FALSE;config.fOutxDsrFlow=FALSE;
    config.fDtrControl=c.protocol==Protocol::SonyH9?DTR_CONTROL_ENABLE:DTR_CONTROL_DISABLE;
    config.fDsrSensitivity=FALSE;config.fTXContinueOnXoff=FALSE;config.fOutX=FALSE;config.fInX=FALSE;
    config.fErrorChar=FALSE;config.fNull=FALSE;config.fRtsControl=c.protocol==Protocol::SonyH9?RTS_CONTROL_ENABLE:RTS_CONTROL_DISABLE;config.fAbortOnError=FALSE;
    timeouts.ReadIntervalTimeout=MAXDWORD;timeouts.ReadTotalTimeoutConstant=50;timeouts.WriteTotalTimeoutConstant=500;
    if(!SetCommState(h.h,&config)||!SetCommTimeouts(h.h,&timeouts))return {};
    unsigned short transaction=2;BCryptGenRandom(nullptr,reinterpret_cast<PUCHAR>(&transaction),sizeof(transaction),BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    auto exchange=[&](unsigned command)->std::optional<Bytes>{
        if(progress)progress();
        transaction=static_cast<unsigned short>(transaction>=65535?2:std::max(2u,unsigned(transaction)+1));
        Bytes packet=c.protocol==Protocol::SonyH9?SonyRequest(command,transaction):BatteryQuery(c,command==4?1:0).packet;
        if(!PurgeComm(h.h,PURGE_RXCLEAR))return {};
        DWORD sent=0;if(!WriteFile(h.h,packet.data(),DWORD(packet.size()),&sent,nullptr)||sent!=packet.size())return {};
        ULONGLONG deadline=GetTickCount64()+1500;Bytes buffer;
        while(GetTickCount64()<deadline&&buffer.size()<512)
        {
            unsigned char chunk[128];DWORD count=0;
            if(!ReadFile(h.h,chunk,c.protocol==Protocol::SonyH9?128:DWORD(64-buffer.size()),&count,nullptr))return {};
            buffer.insert(buffer.end(),chunk,chunk+count);
            if(c.protocol==Protocol::N9Serial)
            {
                if(buffer.size()==64)
                {
                    if(Decode(c,buffer,command==4?1:0))return buffer;buffer.clear();
                }
                continue;
            }
            while(buffer.size()>=3)
            {
                if(buffer[0]!=4||buffer[1]!=0xff||buffer[2]<9||buffer[2]>59){buffer.erase(buffer.begin());continue;}
                size_t size=buffer[2]+3;if(buffer.size()<size)break;
                Bytes frame(buffer.begin(),buffer.begin()+size);buffer.erase(buffer.begin(),buffer.begin()+size);
                if(auto body=SonyPayload(frame,command,transaction))return body;
            }
        }
        return {};
    };
    std::optional<Reading> result;
    if(c.protocol==Protocol::N9Serial)
    {
        for(unsigned round=0;round<2;++round)
        {
            auto link=exchange(4);if(!link)return {};auto online=Decode(c,*link,1);if(!online||online->state!="online")return {};
            auto bytes=exchange(3);if(!bytes)return {};result=Decode(c,*bytes);if(!result)return {};
        }
        auto link=exchange(4);if(!link||(*link)[4]!=1)return {};
    }
    else
    {
        auto link=exchange(1);if(!link||*link!=Bytes{1})return {};
        auto model=exchange(2);if(!model||model->size()!=6||(*model)[0]!=0)return {};
        for(unsigned round=0;round<2;++round)
        {
            auto value=exchange(4);if(!value||value->size()!=2||(*value)[0]>1||(*value)[1]>100)return {};
            Reading r;r.percentage=(*value)[1];r.charging=(*value)[0]!=0;r.estimated=true;result=r;
        }
        auto finalModel=exchange(2);auto finalLink=exchange(1);
        if(!finalModel||!finalLink||*finalModel!=*model||*finalLink!=Bytes{1})return {};
    }
    current.transportPath.clear();AttachSerialTransport(current);
    return current.transportPath==c.transportPath?result:std::nullopt;
}

}
bool IsSpecial(Protocol p){return p==Protocol::OHub||p==Protocol::N9Serial||p==Protocol::SonyH9||p==Protocol::Gaia;}
void AttachSerialTransport(Candidate& c)
{
    bool n9=c.vid==0x373b&&(c.pid==0x11cb||c.pid==0x1204)&&c.mi==4&&c.page==0x0c&&c.usage==1;
    bool h9=c.vid==0x054c&&c.pid==0x0e53&&c.mi==3&&c.page==0x0c&&c.usage==1;
    if(!n9&&!h9)return;
    std::vector<std::string> ports;
    for(const auto& p:Interfaces(ComGuid))
    {
        if(p.vid!=c.vid||p.pid!=c.pid||p.container!=c.physical||(h9&&p.mi!=6)||p.port.size()<4||p.port.size()>8||p.port.rfind("COM",0)!=0||p.port[3]=='0')continue;
        if(!std::all_of(p.port.begin()+3,p.port.end(),[](char ch){return ch>='0'&&ch<='9';}))continue;
        ports.push_back("\\\\.\\"+p.port);
    }
    c.transportPath=ports.size()==1?ports.front():"";
}
std::vector<Candidate> DiscoverSpecial(const std::set<std::string>& excluded, const std::function<void(const std::string&)>& progress)
{
    std::vector<Candidate> result;
    std::map<std::string,std::vector<std::pair<unsigned,Candidate>>> groups;
    for(unsigned priority=0;priority<2;++priority)
        for(const auto& entry:Interfaces(priority?ControlGuid:AudioGuid,excluded,progress))
        {
            if(entry.mi!=0||!OHubId(entry.vid,entry.pid)||entry.path.rfind("\\\\?\\usb#",0)!=0)continue;
            Candidate c;c.vid=entry.vid;c.pid=entry.pid;c.mi=entry.mi;c.path=entry.path+(priority?"":"\\xmosctl");c.physical=entry.container;
            c.protocol=Protocol::OHub;c.name="Misiom O HUB headset";c.kind="headset";c.identity=CanonicalIdentity(c);
            groups[c.physical].push_back({priority,std::move(c)});
        }
    for(auto& group:groups)
    {
        auto& options=group.second;unsigned priority=2;
        for(const auto& item:options)priority=std::min(priority,item.first);
        Candidate* selected=nullptr;unsigned count=0;
        for(auto& item:options)if(item.first==priority){selected=&item.second;++count;}
        if(count==1&&!excluded.count(selected->identity)&&!excluded.count(selected->path))result.push_back(*selected);
    }
    BLUETOOTH_DEVICE_SEARCH_PARAMS search{};search.dwSize=sizeof(search);search.fReturnAuthenticated=TRUE;search.fReturnRemembered=TRUE;
    search.fReturnConnected=TRUE;search.fIssueInquiry=FALSE;
    BLUETOOTH_DEVICE_INFO info{};info.dwSize=sizeof(info);
    HBLUETOOTH_DEVICE_FIND found=BluetoothFindFirstDevice(&search,&info);
    if(found)
    {
        do
        {
            std::string name=Text(info.szName),lower=Lower(name);
            if(lower.find("mmx 230")==std::string::npos||!info.fAuthenticated||!info.fConnected)continue;
            Candidate c;c.protocol=Protocol::Gaia;c.identityOnly=true;c.name=name;c.kind="headset";char address[20]{};
            std::snprintf(address,sizeof(address),"%012llx",info.Address.ullLong);c.physical=address;c.path="service=54943f52-bd0d-48a0-855a-3b5471a6b4ab";
            c.identity=CanonicalIdentity(c);if(!excluded.count(c.identity))result.push_back(std::move(c));
        }while(result.size()<256&&BluetoothFindNextDevice(found,&info));
        BluetoothFindDeviceClose(found);
    }
    return result;
}
std::optional<Reading> ReadSpecial(const Candidate& c, const std::function<void()>& progress)
{
    if(c.identityOnly)return {};
    if(c.protocol==Protocol::OHub)return DriverRead(c,progress);
    if(c.protocol==Protocol::N9Serial||c.protocol==Protocol::SonyH9)return SerialRead(c,progress);
    if(c.protocol==Protocol::Gaia)
    {
        // No immutable model/service identity proof is available in the supplied
        // material. A renameable Bluetooth name never authorizes RFCOMM traffic.
        return {};
    }
    return {};
}
}
