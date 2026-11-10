#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <objbase.h>
#include <setupapi.h>
#include <devpkey.h>
#include <hidsdi.h>
#include <hidpi.h>
#include <algorithm>
#include <memory>
#include <numeric>
#include <random>
#include <stdexcept>
#include "hidapi.h"
#include "tests/HidTrace.h"
#include "ExtraKeyboard.h"

namespace peripheral::extra_keyboard
{
namespace
{
struct Target { unsigned vid,pid; int mi; unsigned page,usage,revision; const char* protocol; const char* product; };
const Target targets[] = {
#include "ExtraKeyboardTargets.inc"
};
bool Prefix(const Bytes& b,std::initializer_list<unsigned char> p)
{return b.size()>=p.size()&&std::equal(p.begin(),p.end(),b.begin());}
unsigned Sum(const Bytes& b,size_t begin,size_t end)
{return std::accumulate(b.begin()+begin,b.begin()+end,0u)&255;}
unsigned Xor(const Bytes& b,size_t begin,size_t end)
{unsigned value=0;for(size_t i=begin;i<end;++i)value^=b[i];return value;}
unsigned U16(const Bytes& b,size_t at){return b[at]|(unsigned(b[at+1])<<8);}
bool Zeros(const Bytes& b,size_t at)
{return at<=b.size()&&std::all_of(b.begin()+at,b.end(),[](unsigned char v){return v==0;});}
Bytes Packet(unsigned size,std::initializer_list<unsigned char> prefix)
{if(size<prefix.size()||size>4096)return {};Bytes b(size,0);std::copy(prefix.begin(),prefix.end(),b.begin());return b;}
bool TargetMatches(const Candidate& c,const Target& t)
{
    return c.vid==t.vid&&c.pid==t.pid&&(t.mi<0||c.mi==t.mi)&&(!t.page||c.page==t.page)&&
        (!t.usage||c.usage==t.usage)&&(!t.revision||t.revision==c.revision)&&(!*t.product||c.usbProduct==t.product);
}
// Native caps include the synthetic report-0 byte; descriptor geometry does not.
bool OnlyIo(const Candidate& c,unsigned id,unsigned input,unsigned output)
{
    if(!c.layout.Has(Report::Input,id,input)||!c.layout.Has(Report::Output,id,output))return false;
    for(const auto& item:c.layout.bytes)
        if((item.first.first==Report::Input||item.first.first==Report::Output)&&item.first.second!=id)return false;
    return true;
}
bool ZeroIo(const Candidate& c,unsigned payload)
{return c.input==payload+1&&c.output==payload+1&&OnlyIo(c,0,payload,payload);}
bool Feature0(const Candidate& c,unsigned payload)
{
    if(c.feature!=payload+1||!c.layout.Has(Report::Feature,0,payload))return false;
    for(const auto& item:c.layout.bytes)if(item.first.first==Report::Feature&&item.first.second)return false;
    return true;
}
unsigned ReportLength(const Candidate& c,Report type,unsigned id)
{auto it=c.layout.bytes.find({type,id});return c.layout.valid&&it!=c.layout.bytes.end()?it->second:0;}
unsigned V4Report(const Candidate& c)
{
    unsigned id=0;
    for(const auto& item:c.layout.bytes)if(item.first.first==Report::Input)
    {if(id||!item.first.second||item.second!=64)return 0;id=item.first.second;}
    return id&&OnlyIo(c,id,64,64)?id:0;
}
bool NeonCollection(const std::string& path)
{
    return path.find("mi_01&col01#")!=std::string::npos;
}
bool Geometry(const Candidate& c,const std::string& p)
{
    if(!c.layout.valid||c.input>4096||c.output>4096||c.feature>4096)return false;
    if(p=="razer_keyboard")return Feature0(c,90);
    if(p=="rog_azoth_x")return ZeroIo(c,64);
    if(p=="vgn_neon75_air")return c.mi==1&&c.page>=0xff00&&Feature0(c,64)&&NeonCollection(c.path);
    if(p=="royuan_legacy_status")return Feature0(c,64);
    if(p=="atk_a87")return ZeroIo(c,32)&&!c.feature;
    if(p=="atk_v75x")return OnlyIo(c,4,64,64)&&c.input>=64&&c.input<=65&&c.output>=64&&c.output<=65;
    if(p=="cherry_mx2pro"||p=="vgn_v98pro")
    {
        unsigned in=ReportLength(c,Report::Input,4),out=ReportLength(c,Report::Output,4);
        unsigned min=p=="cherry_mx2pro"?11:14,minOut=p=="cherry_mx2pro"?11:8;
        return in>=min&&in<=65&&out>=minOut&&out<=65&&c.input>=in&&c.input<=65&&c.output>=out&&c.output<=65&&OnlyIo(c,4,in,out);
    }
    if(p=="mchose_g87")
        // Admit the reader. Read proves the unique same-container writer before any command.
        return c.input>=20&&c.input<=65&&c.layout.Has(Report::Input,0x13,20);
    if(p=="vgn_v98pro_v3")return c.input>=20&&c.input<=65&&c.output>=20&&c.output<=65&&OnlyIo(c,0x13,20,20);
    if(p=="aula_f99pro"||p=="kzzi_k75v2_input")return c.input==20&&c.output==20&&!c.feature&&OnlyIo(c,0x13,20,20);
    if(p=="aula_nova75"||p=="valkyrie_vk75"||p=="weikav_nut75"||p=="aula_hub_keyboard")return ZeroIo(c,32)||ZeroIo(c,64);
    if(p=="vgn_v98pro_v4"||p=="exx_alpha87")return c.input>=64&&c.input<=65&&c.output>=64&&c.output<=65&&V4Report(c);
    if(p=="mchose_qhw")return (c.input==32||c.input==64)&&c.input==c.output&&OnlyIo(c,0x4d,c.input,c.output);
    if(p=="rapoo_v700_input")return c.input==10&&c.output==10&&!c.feature&&c.layout.Has(Report::Input,0xbd,10);
    return false;
}
bool IsFeature(const Candidate& c)
{return c.adapter=="razer_keyboard"||c.adapter=="vgn_neon75_air"||c.adapter=="royuan_legacy_status";}
unsigned InfoSize(const Candidate& c){return c.adapter=="aula_hub_keyboard"?56:48;}
bool IsInfo(const Candidate& c){return c.adapter=="weikav_nut75"||c.adapter=="aula_hub_keyboard";}
unsigned BatteryCommand(const Candidate& c)
{
    const auto& p=c.adapter;
    if(p=="razer_keyboard")return 0x80;
    if(p=="rog_azoth_x"||p=="aula_nova75"||p=="valkyrie_vk75")return 1;
    if(p=="vgn_neon75_air")return 0xf7;
    if(p=="royuan_legacy_status")return 0xfb;
    if(p=="atk_a87")return 0x96;
    if(p=="atk_v75x"||p=="cherry_mx2pro"||p=="vgn_v98pro")return 0x1a;
    if(p=="vgn_v98pro_v3"||p=="mchose_g87"||p=="aula_f99pro")return 0x4a;
    if(p=="kzzi_k75v2_input")return 0x0a;
    if(IsInfo(c))return 0x10;
    if(p=="vgn_v98pro_v4"||p=="exx_alpha87")return 0x87;
    if(p=="mchose_qhw")return 0x904;
    return 0;
}
Bytes Request(const Candidate& c,unsigned command,unsigned token=1)
{
    const auto& p=c.adapter;
    if(command!=BatteryCommand(c)&&!(p=="atk_a87"&&command==0xfd)&&
        !((p=="aula_f99pro"||p=="kzzi_k75v2_input")&&command==5)&&
        !(p=="royuan_legacy_status"&&command==0xfd)&&!(p=="mchose_qhw"&&command==0x900))return {};
    if(p=="razer_keyboard")
    {auto b=Packet(91,{0,0,0x9f,0,0,0,2,7,0x80});b[89]=static_cast<unsigned char>(Xor(b,3,89));return b;}
    if(p=="rog_azoth_x")return Packet(65,{0,0x12,1});
    if(p=="vgn_neon75_air"){auto b=Packet(65,{0,0xf7});b[8]=8;return b;}
    if(p=="royuan_legacy_status")return Packet(65,{0,static_cast<unsigned char>(command)});
    if(p=="atk_a87")return command==0xfd?Packet(33,{0,0xaa,0xfd}):Packet(33,{0,2,0x96,0x24});
    if(p=="atk_v75x")return token&&token<=65535?Packet(c.output,{4,static_cast<unsigned char>(token),static_cast<unsigned char>(token>>8),0x1a}):Bytes{};
    if(p=="cherry_mx2pro")return Packet(c.output,{4,0,0,0x1a,3,0,0,0x55});
    if(p=="vgn_v98pro")return Packet(c.output,{4,0,0,0x1a,6,0,0,0});
    if(p=="mchose_g87"||p=="vgn_v98pro_v3")
    {if(c.output<20)return {};auto b=Packet(c.output,{0x13,0x4a,1});b[19]=0x5e;return b;}
    if(p=="aula_nova75"||p=="valkyrie_vk75")
    {if(c.output!=33&&c.output!=65)return {};auto b=Packet(c.output,{0,0x20,1});b[32]=0x21;return b;}
    if(IsInfo(c))
    {
        if((c.output!=33&&c.output!=65)||token>=InfoSize(c))return {};
        unsigned count=std::min(c.output-9,InfoSize(c)-token);
        return Packet(c.output,{0,0xaa,0x10,static_cast<unsigned char>(count),static_cast<unsigned char>(token),
            static_cast<unsigned char>(token>>8),0,static_cast<unsigned char>(token+count==InfoSize(c))});
    }
    if(p=="aula_f99pro"||p=="kzzi_k75v2_input")
    {
        if(p=="kzzi_k75v2_input"&&command!=5)return {};
        auto b=Packet(20,{0x13,static_cast<unsigned char>(command),1,0,static_cast<unsigned char>(p=="kzzi_k75v2_input"?6:0)});
        b[19]=static_cast<unsigned char>(Sum(b,0,19));return b;
    }
    if(p=="vgn_v98pro_v4"||p=="exx_alpha87")
    {
        unsigned id=V4Report(c);if(!id)return {};
        auto b=Packet(c.output,{static_cast<unsigned char>(id),0x87,0,0,1,0,0});
        b[63]=static_cast<unsigned char>(255-Sum(b,0,63));return b;
    }
    if(p=="mchose_qhw")
    {
        if(token>255)return {};
        auto b=Packet(c.output,{0x4d,1,3,0,static_cast<unsigned char>(command),static_cast<unsigned char>(command>>8),0,static_cast<unsigned char>(token)});
        if(b.size()<9)return {};b[8]=static_cast<unsigned char>(Xor(b,2,8));return b;
    }
    return {};
}
bool Framed19(const Bytes& b)
{return b.size()==20&&b[0]==0x13&&b[2]==1&&!b[3]&&Sum(b,0,19)==b[19];}
bool Invalidates(const Candidate& c,const Bytes& b)
{
    if(c.adapter=="aula_hub_keyboard"&&Prefix(b,{0x55,0xfc})&&b.size()>=3)
        return b[2]==4||b[2]==5||b[2]==6;
    return c.adapter=="kzzi_k75v2_input"&&Framed19(b)&&b[1]==0x0a&&b[5]==2;
}
// Unnumbered input/feature bodies omit report 0; numbered reports retain ID.
// AA10 returns only the correlated data block.
std::optional<Bytes> Payload(const Candidate& c,const Bytes& raw,unsigned command,unsigned token=1)
{
    auto b=raw;const auto& p=c.adapter;
    if(IsFeature(c))
    {
        if(b.size()!=c.feature||b.empty()||b[0])return {};
        if(p=="razer_keyboard")
        {
            if(b.size()!=91||!Prefix(b,{0,2,0x9f,0,0,0,2,7,0x80})||b[90]||Xor(b,3,89)!=b[89])return {};
            return b;
        }
        b.erase(b.begin());
        if(p=="royuan_legacy_status"&&command==0xfd&&(!Prefix(b,{0xaa,0x55})||!Zeros(b,2)))return {};
        auto request=Request(c,command,token);
        if(request.size()==raw.size()&&std::equal(b.begin(),b.end(),request.begin()+1))return {};
        return b;
    }
    if(p=="rog_azoth_x")
    {
        if(b.size()!=64||!Prefix(b,{0x12,1}))return {};
        auto q=Request(c,command);if(std::equal(b.begin(),b.end(),q.begin()+1))return {};return b;
    }
    if(p=="atk_a87")
    {
        if(b.size()!=32)return {};
        if(command==0xfd){if(!Prefix(b,{0x55,0xfd})||b[2]!=1)return {};return b;}
        auto q=Request(c,command);if(!Prefix(b,{2,0x96,0x24})||std::equal(b.begin(),b.end(),q.begin()+1))return {};return b;
    }
    if(p=="atk_v75x")
    {
        if((b.size()!=64&&b.size()!=65)||!Zeros(b,64)||!Prefix(b,{4,static_cast<unsigned char>(token),static_cast<unsigned char>(token>>8),0x1a}))return {};
        b.resize(64);auto q=Request(c,command,token);if(q.size()<64||std::equal(b.begin(),b.end(),q.begin()))return {};return b;
    }
    if(p=="cherry_mx2pro"||p=="vgn_v98pro")
    {
        unsigned size=ReportLength(c,Report::Input,4);
        if(size<8||(b.size()!=size&&b.size()!=c.input)||b.size()<size||!Zeros(b,size)||b[0]!=4||b[3]!=0x1a)return {};
        if(p=="cherry_mx2pro"&&!std::equal(b.begin()+3,b.begin()+8,Bytes{0x1a,3,0,0,0x55}.begin()))return {};
        if(p=="vgn_v98pro"&&(b.size()<14||std::all_of(b.begin()+8,b.begin()+14,[](unsigned char x){return !x;})))return {};
        auto q=Request(c,command);if(b==q)return {};return b;
    }
    if(p=="mchose_g87"||p=="vgn_v98pro_v3")
    {
        if((b.size()!=20&&b.size()!=c.input)||b.size()<20||!Zeros(b,20)||!Prefix(b,{0x13,0x4a,1,0}))return {};
        b.resize(20);auto q=Request(c,command);if(q.size()<20||std::equal(b.begin(),b.end(),q.begin()))return {};return b;
    }
    if(p=="aula_nova75"||p=="valkyrie_vk75")
    {if(b.size()!=c.input-1||!Prefix(b,{0x20,1,0})||!b[3])return {};return b;}
    if(IsInfo(c))
    {
        if(token>=InfoSize(c)||c.output<9)return {};
        unsigned count=std::min(c.output-9,InfoSize(c)-token);
        if(b.size()!=c.input-1||!Prefix(b,{0x55,0x10})||b[2]!=count||U16(b,3)!=token||b.size()<8+count)return {};
        return Bytes(b.begin()+8,b.begin()+8+count);
    }
    if(p=="aula_f99pro"||p=="kzzi_k75v2_input")
    {
        unsigned length=command==5?6:command==0x4a?1:4;
        if(!Framed19(b)||b[1]!=command||b[4]!=length||b==Request(c,command))return {};
        return Bytes(b.begin()+5,b.begin()+5+length);
    }
    if(p=="vgn_v98pro_v4"||p=="exx_alpha87")
    {
        unsigned id=V4Report(c);
        if(!id||(b.size()!=64&&b.size()!=c.input)||b.size()<64||!Zeros(b,64)||b[0]!=id||b[1]!=0x87||b[2]||b[4]!=1||b[5]||b[6]<2||b[6]>56)return {};
        b.resize(64);auto q=Request(c,command);if(q.size()<64||std::equal(b.begin(),b.end(),q.begin()))return {};return b;
    }
    if(p=="mchose_qhw")
    {
        if(b.size()!=c.input||b.size()<32||!Prefix(b,{0x4d,1,3})||!b[3]||b[3]>23||b[4]!=(command&255)||b[5]!=(command>>8)||b[6]||b[7]!=token)return {};
        return Bytes(b.begin()+8,b.begin()+8+b[3]);
    }
    if(p=="rapoo_v700_input")
    {if(b.size()!=10||!Prefix(b,{0xbd,1}))return {};return b;}
    return {};
}
std::vector<unsigned> Boards(const Candidate& c)
{
    static const std::map<std::string,std::vector<unsigned>> boards={
        {"F75 Master Dongle",{0x2758}},{"AULA F87ProV2C Dongle",{0x2712,0x2738,0x2747}},
        {"AULA F87ProV2D Dongle",{0x2711,0x2737,0x2746}},{"AULA F87ProV2Z Dongle",{0x2710,0x2736,0x2748}},
        {"AULA F108Pro V2 Dongle",{0x271a,0x2739}},{"AULA F108ProV2C Dongle",{0x273a,0x2749}},
        {"AULA NOVA75 Pro",{0x275c}},{"F75 V2 Dongle",{0x273f}},{"AULA F75 V2 Dongle",{0x2768}},
        {"AULA NOVA98 Dongle",{0x273b,0x275d}}};
    auto it=boards.find(c.usbProduct);return it==boards.end()?std::vector<unsigned>{}:it->second;
}
std::optional<Reading> DecodeBattery(const Candidate& c,const Bytes& b)
{
    Reading r;unsigned value=255;const auto& p=c.adapter;
    if(p=="razer_keyboard"){if(b.size()!=91)return {};value=(unsigned(b[10])*100+127)/255;}
    else if(p=="rog_azoth_x"){if(b.size()!=64||b[8]>1)return {};value=b[5];r.charging=b[8]==1;}
    else if(p=="vgn_neon75_air")
    {if(b.size()!=64||b[0]>1||b[3]||b[5]>1||b[9]>1||!b[1])return {};value=b[1];r.charging=b[9]!=0;}
    else if(p=="royuan_legacy_status")
    {if(b.size()!=64||b[0]||b[2]||b[3]!=1||!Zeros(b,4)||!b[1])return {};value=b[1];}
    else if(p=="atk_a87")
    {
        if(b.size()!=32||b[3]>100||b[4]>1)return {};
        // Vendor omits numeric percentage while charging; charge indication remains unverified.
        if(b[4])return r;value=b[3];
    }
    else if(p=="atk_v75x"){if(b.size()!=64||b[9]>2)return {};value=b[8];r.charging=b[9]==2;}
    else if(p=="cherry_mx2pro"){if(b.size()<11||!b[8])return {};value=b[8];}
    else if(p=="vgn_v98pro"){if(b.size()<14)return {};value=b[8];}
    else if(p=="mchose_g87"||p=="vgn_v98pro_v3")
    {
        if(b.size()!=20)return {};value=b[5];
        if(p=="mchose_g87"&&((b[6]>>4)>1||(b[6]&15)>1))return {};
        // G87/K99 charge bits have reported false positives; VGN V3 charge semantics are unknown.
    }
    else if(p=="aula_nova75"||p=="valkyrie_vk75"){if(b.size()<4||!b[3])return {};value=b[3];}
    else if(IsInfo(c))
    {
        if(b.size()!=InfoSize(c))return {};value=b[17];
        if(p=="aula_hub_keyboard")
        {auto boards=Boards(c);if(U16(b,4)!=0x38a6||std::find(boards.begin(),boards.end(),U16(b,6))==boards.end()||b[18]>2)return {};r.charging=b[18]==1||b[18]==2;}
    }
    else if(p=="aula_f99pro"){if(b.size()!=1)return {};value=b[0];}
    else if(p=="kzzi_k75v2_input"){if(b.size()!=4||b[0]!=5||(b[2]&0xf0)==0x10)return {};value=b[1];}
    else if(p=="vgn_v98pro_v4"||p=="exx_alpha87")
    {if(b.size()!=64)return {};value=b[7];if(p=="exx_alpha87"){if(b[8]!=0&&b[8]!=1&&b[8]!=0x10&&b[8]!=0x11)return {};r.charging=(b[8]&0xf0)!=0;}}
    else if(p=="mchose_qhw"){if(b.size()!=2||b[0]>2)return {};value=b[1];r.charging=b[0]==1;}
    else if(p=="rapoo_v700_input"){if(b.size()!=10||(b[7]!=1&&b[7]!=2))return {};value=b[8];r.charging=b[7]==2;}
    if(value>100)return {};r.percentage=int(value);return r;
}

// The callable boundary exercises complete multi-query transactions in offline tests.
using Call=std::function<std::optional<Bytes>(unsigned,unsigned,bool)>;
std::optional<Reading> Run(const Candidate& c,const Call& call,unsigned seed)
{
    const auto& p=c.adapter;unsigned sequence=seed;
    auto next=[&](){sequence=sequence%65535+1;return p=="mchose_qhw"?(sequence%255+1):sequence;};
    Bytes identity;
    auto identify=[&]()->bool
    {
        if(p=="aula_f99pro"||p=="kzzi_k75v2_input")
        {auto b=call(5,1,false);return b&&*b==(p=="aula_f99pro"?Bytes{3,0,0,0,1,0x58}:Bytes{3,0,0,0,3,0xa2});}
        if(p=="royuan_legacy_status")return bool(call(0xfd,1,false));
        if(p=="mchose_qhw")
        {
            auto b=call(0x900,next(),false);
            unsigned board=c.usbProduct=="MCHOSE K87 V3 2.4G"?0x2023:0x2025;
            if(!b||b->size()<16||U16(*b,0)!=0x3837||(U16(*b,2)!=0x1015&&U16(*b,2)!=board)||(*b)[10]!=2||(*b)[11]>2||(*b)[12]>100)return false;
            Bytes current(b->begin(),b->begin()+11);if(!identity.empty()&&identity!=current)return false;identity=std::move(current);
        }
        return true;
    };
    if(!identify())return {};
    if((p=="aula_f99pro"||p=="kzzi_k75v2_input")&&!identify())return {};
    std::optional<Reading> reading;
    for(unsigned round=0;round<2;++round)
    {
        if(p=="atk_a87"&&!c.wired&&!call(0xfd,1,false))return {};
        std::optional<Bytes> bytes;
        if(IsInfo(c))
        {
            Bytes assembled;
            for(unsigned address=0;address<InfoSize(c);)
            {
                unsigned count=std::min(c.output-9,InfoSize(c)-address);
                auto block=call(0x10,address,false);if(!block||block->size()!=count)return {};
                assembled.insert(assembled.end(),block->begin(),block->end());address+=count;
            }
            bytes=std::move(assembled);
            if(p=="aula_hub_keyboard")
            {
                Bytes current(bytes->begin()+4,bytes->begin()+10);current.insert(current.end(),bytes->begin()+12,bytes->begin()+16);
                if(!identity.empty()&&identity!=current)return {};identity=std::move(current);
            }
        }
        else bytes=call(BatteryCommand(c),next(),p=="rapoo_v700_input"||p=="kzzi_k75v2_input");
        if(!bytes)return {};auto current=DecodeBattery(c,*bytes);if(!current)return {};
        if(p=="royuan_legacy_status"&&reading&&reading->percentage!=current->percentage)return {};
        reading=std::move(current);
    }
    if(p=="atk_a87"&&!c.wired&&!call(0xfd,1,false))return {};
    if(!identify())return {};return reading;
}

struct Device
{
    hid_device* h;
    explicit Device(const std::string& path):h(hid_open_path(path.c_str())){}
    ~Device(){if(h)hid_close(h);}
    Device(const Device&)=delete;Device& operator=(const Device&)=delete;
};
std::string Utf8(const wchar_t* text)
{
    if(!text)return {};int n=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text,-1,nullptr,0,nullptr,nullptr);
    if(n<=1)return {};std::string value(n,0);if(!WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text,-1,value.data(),n,nullptr,nullptr))return {};value.pop_back();return value;
}
std::string Container(const std::string& path)
{
    int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,path.c_str(),-1,nullptr,0);if(n<=1)return {};
    std::wstring wide(n,0);if(!MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,path.c_str(),-1,wide.data(),n))return {};
    HDEVINFO list=SetupDiCreateDeviceInfoList(nullptr,nullptr);if(list==INVALID_HANDLE_VALUE)return {};
    SP_DEVICE_INTERFACE_DATA iface{};iface.cbSize=sizeof(iface);SP_DEVINFO_DATA info{};info.cbSize=sizeof(info);
    DWORD size=0;GUID id{};DEVPROPTYPE type=0;bool valid=false;
    if(SetupDiOpenDeviceInterfaceW(list,wide.c_str(),0,&iface))
    {
        SetupDiGetDeviceInterfaceDetailW(list,&iface,nullptr,0,&size,nullptr);
        if(size>=sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)&&size<=32768)
        {
            std::vector<unsigned char> data(size);auto detail=reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(data.data());detail->cbSize=sizeof(*detail);
            valid=SetupDiGetDeviceInterfaceDetailW(list,&iface,detail,size,nullptr,&info)&&
                SetupDiGetDevicePropertyW(list,&info,&DEVPKEY_Device_ContainerId,&type,reinterpret_cast<PBYTE>(&id),sizeof(id),&size,0)&&type==DEVPROP_TYPE_GUID&&!IsEqualGUID(id,GUID_NULL);
        }
    }
    SetupDiDestroyDeviceInfoList(list);if(!valid)return {};
    wchar_t text[40]{};if(!StringFromGUID2(id,text,40))return {};auto value=Utf8(text);
    std::transform(value.begin(),value.end(),value.begin(),[](unsigned char ch){return ch>='A'&&ch<='Z'?char(ch+32):char(ch);});return value;
}
// M HUB can split report19 input/output between FF02/FF04 collections. Resolve
// only this receiver's current container, exact product/revision and actual reports.
struct MchoseChannels {Candidate input,output;};
std::optional<MchoseChannels> SelectMchose(const Candidate& c,const std::vector<Candidate>& peers)
{
    if(peers.empty()||peers.size()>3)return {};
    MchoseChannels result;unsigned readers=0,writers=0,anchors=0;
    for(const auto& peer:peers)
    {
        if(peer.physical!=c.physical||peer.vid!=c.vid||peer.pid!=c.pid||peer.mi!=c.mi||peer.usage!=2||
            (peer.page!=0xff02&&peer.page!=0xff04)||peer.revision!=c.revision||peer.usbProduct!=c.usbProduct||!peer.layout.valid)return {};
        bool allowed=false;for(const auto& t:targets)if(std::string(t.protocol)=="mchose_g87"&&TargetMatches(peer,t))allowed=true;
        if(!allowed)return {};
        if(peer.page==(c.vid==0x3837?0xff02u:0xff04u))++anchors;
        if(peer.input>=20&&peer.input<=65&&peer.layout.Has(Report::Input,0x13,20)){++readers;result.input=peer;}
        if(peer.output>=20&&peer.output<=65&&peer.layout.Has(Report::Output,0x13,20)){++writers;result.output=peer;}
    }
    if(!anchors||readers!=1||writers!=1||result.input.path!=c.path)return {};
    return result;
}
std::optional<MchoseChannels> MchosePeers(const Candidate& c,const std::function<void()>& progress,ULONGLONG deadline)
{
    if(c.physical.empty()||Container(c.path)!=c.physical)return {};
    auto head=hid_enumerate(static_cast<unsigned short>(c.vid),static_cast<unsigned short>(c.pid));
    struct List{hid_device_info* h;~List(){hid_free_enumeration(h);}} list{head};
    std::vector<Candidate> peers;unsigned visited=0;
    for(auto p=head;p;p=p->next)
    {
        if(++visited>256||GetTickCount64()>=deadline)return {};if(progress)progress();
        if(!p->path||p->vendor_id!=c.vid||p->product_id!=c.pid||p->interface_number!=c.mi||p->usage!=2||
            (p->usage_page!=0xff02&&p->usage_page!=0xff04))continue;
        auto container=Container(p->path);if(container.empty())return {};if(container!=c.physical)continue;
        if(peers.size()>=3||p->release_number!=c.revision||Utf8(p->product_string)!=c.usbProduct)return {};
        Candidate peer=c;peer.path=p->path;peer.page=p->usage_page;peer.usage=p->usage;
        std::transform(peer.path.begin(),peer.path.end(),peer.path.begin(),[](unsigned char ch){return ch>='A'&&ch<='Z'?char(ch+32):char(ch);});
        bool allowed=false;for(const auto& t:targets)if(std::string(t.protocol)=="mchose_g87"&&TargetMatches(peer,t))allowed=true;
        if(!allowed)return {};
        int chars=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,p->path,-1,nullptr,0);if(chars<=1)return {};
        std::wstring path(chars,0);if(!MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,p->path,-1,path.data(),chars))return {};
        HANDLE handle=CreateFileW(path.c_str(),0,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,0,nullptr);
        if(handle==INVALID_HANDLE_VALUE)return {};
        HIDD_ATTRIBUTES attributes{};attributes.Size=sizeof(attributes);PHIDP_PREPARSED_DATA data=nullptr;HIDP_CAPS caps{};
        bool valid=HidD_GetAttributes(handle,&attributes)&&attributes.VendorID==c.vid&&attributes.ProductID==c.pid&&attributes.VersionNumber==c.revision&&
            HidD_GetPreparsedData(handle,&data);
        if(valid)valid=HidP_GetCaps(data,&caps)==HIDP_STATUS_SUCCESS&&caps.UsagePage==peer.page&&caps.Usage==peer.usage;
        wchar_t product[256]{};if(valid)valid=HidD_GetProductString(handle,product,sizeof(product))&&Utf8(product)==c.usbProduct;
        if(data)HidD_FreePreparsedData(data);CloseHandle(handle);if(!valid)return {};
        peer.input=caps.InputReportByteLength;peer.output=caps.OutputReportByteLength;peer.feature=caps.FeatureReportByteLength;
        Device device(peer.path);if(!device.h)return {};Bytes descriptor(HID_API_MAX_REPORT_DESCRIPTOR_SIZE);
        int count=hid_get_report_descriptor(device.h,descriptor.data(),descriptor.size());if(count<=0)return {};descriptor.resize(count);peer.layout=ParseDescriptor(descriptor);
        if(!peer.layout.valid)return {};peers.push_back(std::move(peer));
    }
    return SelectMchose(c,peers);
}
bool SameChannel(const Candidate& a,const Candidate& b)
{return a.path==b.path&&a.revision==b.revision&&a.input==b.input&&a.output==b.output&&a.feature==b.feature&&a.layout.bytes==b.layout.bytes;}
}

bool KnownIdentity(unsigned vid,unsigned pid)
{for(const auto& t:targets)if(t.vid==vid&&t.pid==pid)return true;return false;}
bool Admit(Candidate& c)
{
    const Target* chosen=nullptr;
    for(const auto& t:targets)
    {
        if(!TargetMatches(c,t)||!Geometry(c,t.protocol))continue;
        if(chosen&&std::string(chosen->protocol)!=t.protocol)return false;chosen=&t;
    }
    if(!chosen)return false;
    c.protocol=Protocol::ExtraKeyboard;c.adapter=chosen->protocol;c.kind="keyboard";
    c.name=c.usbProduct.empty()?"Wireless keyboard":c.usbProduct;
    if(c.adapter=="atk_a87")c.wired=c.pid==0x111e||c.pid==0x114d||c.pid==0x1151||c.pid==0x1231;
    if(c.adapter=="rog_azoth_x")c.wired=c.pid==0x1a83||c.pid==0x1c24;
    return true;
}
std::optional<Reading> Read(const Candidate& candidate,const std::function<void()>& progress)
{
    Candidate c=candidate;
    if(!Admit(c)||c.adapter!=candidate.adapter)return {};
    const ULONGLONG totalDeadline=GetTickCount64()+10000;
    std::optional<MchoseChannels> peers;
    if(c.adapter=="mchose_g87")
    {
        peers=MchosePeers(c,progress,totalDeadline);if(!peers)return {};
        c.input=peers->input.input;c.output=peers->output.output;
    }
    if(progress)progress();Device reader(c.path);if(!reader.h)return {};
    std::unique_ptr<Device> writer;
    if(peers&&peers->output.path!=c.path){writer=std::make_unique<Device>(peers->output.path);if(!writer->h)return {};}
    hid_device* out=writer?writer->h:reader.h;bool invalidated=false,passiveStarted=false;
    auto drain=[&]()->bool
    {
        for(unsigned i=0;i<32;++i)
        {
            if(GetTickCount64()>=totalDeadline)return false;if(progress)progress();
            unsigned char buffer[4096];int n=hid_read_timeout(reader.h,buffer,sizeof(buffer),1);
            if(n<0)return false;if(!n)return true;
            if(Invalidates(c,Bytes(buffer,buffer+n))){invalidated=true;return false;}
        }
        return false;
    };
    Call exchange=[&](unsigned command,unsigned token,bool passive)->std::optional<Bytes>
    {
        if(invalidated||GetTickCount64()>=totalDeadline)return {};if(progress)progress();
        if(passive)
        {if(!passiveStarted){if(!drain())return {};passiveStarted=true;}}
        else
        {
            if(!IsFeature(c)&&!drain())return {};
            auto packet=Request(c,command,token);if(packet.empty())return {};
            int count=IsFeature(c)?hid_send_feature_report(out,packet.data(),packet.size()):hid_write(out,packet.data(),packet.size());
            if(count!=int(packet.size()))return {};
        }
        ULONGLONG deadline=std::min(totalDeadline,GetTickCount64()+(passive?2000ULL:IsInfo(c)?500ULL:1000ULL));
        for(unsigned attempts=0;attempts<64&&GetTickCount64()<deadline;++attempts)
        {
            if(progress)progress();Bytes bytes(IsFeature(c)?c.feature:std::max(c.input,65u),0);
            int n;
            if(IsFeature(c)){Sleep(20);if(GetTickCount64()>=deadline)return {};n=hid_get_feature_report(reader.h,bytes.data(),bytes.size());}
            else n=hid_read_timeout(reader.h,bytes.data(),bytes.size(),40);
            if(n<0)return {};if(!n)continue;bytes.resize(n);
            if(Invalidates(c,bytes)){invalidated=true;return {};}
            if(auto payload=Payload(c,bytes,command,token))return payload;
        }
        return {};
    };
    std::random_device random;
    auto result=Run(c,exchange,random()%65535);
    if(!result||invalidated||GetTickCount64()>=totalDeadline)return {};
    if(c.adapter=="aula_hub_keyboard"&&!drain())return {};
    if(peers)
    {
        auto after=MchosePeers(candidate,progress,totalDeadline);
        if(!after||!SameChannel(peers->input,after->input)||!SameChannel(peers->output,after->output))return {};
    }
    if(GetTickCount64()>=totalDeadline)return {};return result;
}

namespace
{
Candidate Fixture(const Target& t)
{
    Candidate c;c.vid=t.vid;c.pid=t.pid;c.mi=t.mi<0?1:t.mi;c.page=t.page?t.page:0xff60;c.usage=t.usage?t.usage:0x61;
    c.revision=t.revision?t.revision:0x7777;c.usbProduct=t.product;c.adapter=t.protocol;c.path="fixture&col01";c.physical="fixture";c.layout.valid=true;
    const auto& p=c.adapter;
    if(p=="vgn_neon75_air")c.path="fixture#mi_01&col01#fixture";
    auto io=[&](unsigned id,unsigned size){c.input=c.output=size+(id?0:1);c.layout.bytes[{Report::Input,id}]=size;c.layout.bytes[{Report::Output,id}]=size;};
    if(p=="razer_keyboard"||p=="vgn_neon75_air"||p=="royuan_legacy_status")
    {unsigned size=p=="razer_keyboard"?90:64;c.feature=size+1;c.layout.bytes[{Report::Feature,0}]=size;}
    else if(p=="rapoo_v700_input"){c.input=c.output=10;c.layout.bytes[{Report::Input,0xbd}]=10;}
    else if(p=="rog_azoth_x")io(0,64);
    else if(p=="atk_a87"||p=="aula_nova75"||p=="valkyrie_vk75"||IsInfo(c))io(0,32);
    else if(p=="atk_v75x")io(4,64);
    else if(p=="cherry_mx2pro"||p=="vgn_v98pro")io(4,32);
    else if(p=="mchose_qhw")io(0x4d,32);
    else if(p=="vgn_v98pro_v4"||p=="exx_alpha87")io(9,64);
    else io(0x13,20);
    return c;
}
Bytes FixtureInfo(const Candidate& c)
{
    Bytes b(InfoSize(c),0);b[17]=66;b[18]=1;
    if(c.adapter=="aula_hub_keyboard")
    {auto boards=Boards(c);if(boards.empty())return {};b[4]=0xa6;b[5]=0x38;b[6]=static_cast<unsigned char>(boards[0]);b[7]=static_cast<unsigned char>(boards[0]>>8);}
    return b;
}
Bytes FixtureReply(const Candidate& c,unsigned command,unsigned token)
{
    const auto& p=c.adapter;
    if(p=="razer_keyboard")
    {auto b=Request(c,command,token);b[1]=2;b[10]=168;b[89]=static_cast<unsigned char>(Xor(b,3,89));return b;}
    if(p=="rog_azoth_x"){auto b=Packet(64,{0x12,1});b[5]=66;b[8]=1;return b;}
    if(p=="vgn_neon75_air"){auto b=Packet(65,{0});b[2]=66;b[10]=1;return b;}
    if(p=="royuan_legacy_status")return command==0xfd?Packet(65,{0,0xaa,0x55}):Packet(65,{0,0,66,0,1});
    if(p=="atk_a87")return command==0xfd?Packet(32,{0x55,0xfd,1}):Packet(32,{2,0x96,0x24,66,0});
    if(p=="rapoo_v700_input"){auto b=Packet(10,{0xbd,1});b[7]=1;b[8]=66;return b;}
    if(p=="atk_v75x"||p=="cherry_mx2pro"||p=="vgn_v98pro")
    {auto b=Request(c,command,token);b.resize(ReportLength(c,Report::Input,4));b[8]=66;if(p=="atk_v75x")b[9]=2;return b;}
    if(p=="mchose_g87"||p=="vgn_v98pro_v3")
    {auto b=Packet(20,{0x13,0x4a,1,0,0,66,0x11});b[19]=static_cast<unsigned char>(Sum(b,0,19));return b;}
    if(p=="aula_f99pro"||p=="kzzi_k75v2_input")
    {
        auto b=Packet(20,{0x13,static_cast<unsigned char>(command),1,0,static_cast<unsigned char>(command==5?6:command==0x4a?1:4)});
        Bytes data=command==5?(p=="aula_f99pro"?Bytes{3,0,0,0,1,0x58}:Bytes{3,0,0,0,3,0xa2}):command==0x4a?Bytes{66}:Bytes{5,66,0,0};
        std::copy(data.begin(),data.end(),b.begin()+5);b[19]=static_cast<unsigned char>(Sum(b,0,19));return b;
    }
    if(p=="aula_nova75"||p=="valkyrie_vk75")return Packet(c.input-1,{0x20,1,0,66});
    if(IsInfo(c))
    {
        auto data=FixtureInfo(c);if(data.empty()||token>=data.size())return {};
        unsigned count=std::min(c.output-9,unsigned(data.size())-token);
        auto b=Packet(c.input-1,{0x55,0x10,static_cast<unsigned char>(count),static_cast<unsigned char>(token),static_cast<unsigned char>(token>>8)});
        std::copy_n(data.begin()+token,count,b.begin()+8);return b;
    }
    if(p=="vgn_v98pro_v4"||p=="exx_alpha87")
    {auto b=Packet(64,{static_cast<unsigned char>(V4Report(c)),0x87,0,0,1,0,2,66,static_cast<unsigned char>(p=="exx_alpha87"?0x10:0)});return b;}
    if(p=="mchose_qhw")
    {
        auto b=Packet(c.input,{0x4d,1,3,static_cast<unsigned char>(command==0x900?16:2),static_cast<unsigned char>(command),static_cast<unsigned char>(command>>8),0,static_cast<unsigned char>(token)});
        Bytes data=command==0x900?Bytes{0x37,0x38,0x25,0x20,0,0,0,0,0,0,2,1,66,0,0,0}:Bytes{1,66};
        if(command==0x900&&c.usbProduct=="MCHOSE K87 V3 2.4G")data[2]=0x23;
        std::copy(data.begin(),data.end(),b.begin()+8);return b;
    }
    return {};
}
}

void OfflineSelfTests()
{
    auto check=[](bool valid,const char* reason){if(!valid)throw std::runtime_error(reason);};
    std::set<std::string> families;
    for(const auto& target:targets)
    {
        auto c=Fixture(target);check(Admit(c)&&c.protocol==Protocol::ExtraKeyboard&&c.adapter==target.protocol&&c.kind=="keyboard","keyboard target geometry/name admission");
        families.insert(c.adapter);
        auto bad=c;bad.vid=0xffff;check(!Admit(bad),"no brand-wide VID/PID admission");
        bad=c;bad.layout.valid=false;check(!Admit(bad),"descriptor required");
        if(target.mi>=0){bad=c;bad.mi+=10;check(!Admit(bad),"interface evidence required");}
        if(target.page){bad=c;bad.page^=1;check(!Admit(bad),"usage-page evidence required");}
        if(target.usage){bad=c;bad.usage^=0x100;check(!Admit(bad),"usage evidence required");}
        if(*target.product){bad=c;bad.usbProduct="unlisted receiver";check(!Admit(bad),"exact shared-PID product required");}
        if(target.revision){bad=c;bad.revision=0xeeee;check(!Admit(bad),"explicit model/generic-name revision required");}
        const unsigned command=BatteryCommand(c),token=0x23;
        auto frame=FixtureReply(c,command,IsInfo(c)?0:token);
        auto payload=Payload(c,frame,command,IsInfo(c)?0:token);
        check(bool(payload),"complete keyboard reply fixture");
        if(!IsInfo(c))
        {
            auto r=DecodeBattery(c,*payload);check(r&&r->percentage==66,"keyboard percentage fixture");
            if(c.adapter=="mchose_g87"||c.adapter=="vgn_v98pro_v3"||c.adapter=="vgn_v98pro"||c.adapter=="weikav_nut75")
                check(!r->charging.has_value(),"unknown charging stays nullable");
        }
        if(c.adapter!="razer_keyboard")
        {
            auto numeric=IsInfo(c)?FixtureInfo(c):*payload;
            static const std::map<std::string,unsigned> offsets={
                {"rog_azoth_x",5},{"vgn_neon75_air",1},{"royuan_legacy_status",1},{"atk_a87",3},
                {"atk_v75x",8},{"cherry_mx2pro",8},{"vgn_v98pro",8},{"mchose_g87",5},{"vgn_v98pro_v3",5},
                {"aula_f99pro",0},{"kzzi_k75v2_input",1},{"aula_nova75",3},{"valkyrie_vk75",3},
                {"weikav_nut75",17},{"aula_hub_keyboard",17},{"vgn_v98pro_v4",7},{"exx_alpha87",7},
                {"mchose_qhw",1},{"rapoo_v700_input",8}};
            numeric[offsets.at(c.adapter)]=101;check(!DecodeBattery(c,numeric),"out-of-range keyboard percentage rejected");
        }
        for(unsigned length:{0u,1u,2u})
        {auto truncated=frame;truncated.resize(length);check(!Payload(c,truncated,command,IsInfo(c)?0:token),"truncated keyboard header rejected");}
        auto wrong=frame;wrong[0]^=1;check(!Payload(c,wrong,command,IsInfo(c)?0:token),"foreign report/header rejected");
        auto query=Request(c,command,IsInfo(c)?0:token);
        if(!query.empty())check(!Payload(c,query,command,IsInfo(c)?0:token),"query echo rejected");
        unsigned batteries=0,identities=0,status=0;
        Call fake=[&](unsigned cmd,unsigned seq,bool passive)->std::optional<Bytes>
        {
            if(cmd==command)++batteries;else if(cmd==5||cmd==0x900)++identities;else if(cmd==0xfd)++status;
            check(passive==((c.adapter=="rapoo_v700_input"||c.adapter=="kzzi_k75v2_input")&&cmd==command),"notification-only protocols never write their battery command");
            return Payload(c,FixtureReply(c,cmd,seq),cmd,seq);
        };
        auto result=Run(c,fake,0xffff);check(result&&result->percentage==66,"two effective keyboard confirmations");
        if(c.adapter=="mchose_qhw")
        {
            Call foreignBoard=[&](unsigned cmd,unsigned seq,bool)->std::optional<Bytes>
            {auto raw=FixtureReply(c,cmd,seq);if(cmd==0x900)raw[10]=c.usbProduct=="MCHOSE K87 V3 2.4G"?0x25:0x23;return Payload(c,raw,cmd,seq);};
            check(!Run(c,foreignBoard,1),"QHW model-specific paired board rejected");
        }
        if(c.adapter=="vgn_neon75_air"){auto missingCollection=c;missingCollection.path="fixture#mi_01#fixture";check(!Admit(missingCollection),"Neon requires explicit col01");}
        check(batteries==(IsInfo(c)?2*((InfoSize(c)+c.output-10)/(c.output-9)):2),"all battery/info rounds completed");
        if(c.adapter=="aula_f99pro"||c.adapter=="kzzi_k75v2_input")check(identities==3,"two initial plus final FA09 identities");
        if(c.adapter=="atk_a87")check(status==(c.wired?0:3),"wireless final link guard and no wired status request");
        unsigned seen=0;
        Call interrupted=[&](unsigned cmd,unsigned seq,bool)->std::optional<Bytes>
        {if(cmd==command&&++seen==2)return {};return Payload(c,FixtureReply(c,cmd,seq),cmd,seq);};
        check(!Run(c,interrupted,1),"partial confirmation cannot publish a reading");
    }
    check(families.size()==20,"all assigned families plus QHW retained");

    auto find=[&](const std::string& name)
    {for(const auto& t:targets)if(name==t.protocol){auto c=Fixture(t);check(Admit(c),"fixture admission");return c;}throw std::runtime_error("missing keyboard fixture");};
    auto c=find("atk_v75x");auto b=FixtureReply(c,0x1a,0x1234);
    check(!Payload(c,b,0x1a,0x1235),"V75X stale sequence");b[9]=3;
    auto p=Payload(c,b,0x1a,0x1234);check(p&&!DecodeBattery(c,*p),"V75X invalid charging enum");
    c=find("razer_keyboard");b=FixtureReply(c,0x80,1);b[89]^=1;check(!Payload(c,b,0x80),"Razer XOR");
    b=FixtureReply(c,0x80,1);b[2]=0x1f;check(!Payload(c,b,0x80),"Razer keyboard transaction differs from mouse");
    c=find("vgn_neon75_air");b=FixtureReply(c,0xf7,1);b[4]=1;p=Payload(c,b,0xf7);check(p&&!DecodeBattery(c,*p),"Neon disconnected");
    b=FixtureReply(c,0xf7,1);b[2]=0;p=Payload(c,b,0xf7);check(p&&!DecodeBattery(c,*p),"Neon uninitialized zero");
    auto wrongCollection=c;wrongCollection.path="fixture&col010";check(!Admit(wrongCollection),"Neon collection is a complete token");
    c=find("royuan_legacy_status");b=FixtureReply(c,0xfd,1);b.back()=1;check(!Payload(c,b,0xfd),"Royuan wireless service tail");
    b=FixtureReply(c,0xfb,1);b[3]=1;p=Payload(c,b,0xfb);check(p&&!DecodeBattery(c,*p),"Royuan offline flag");
    c=find("cherry_mx2pro");check(Geometry(c,c.adapter),"Cherry 32-byte reports supported");
    c.input=c.output=11;c.layout.bytes[{Report::Input,4}]=11;c.layout.bytes[{Report::Output,4}]=11;check(Admit(c),"Cherry descriptor-selected 11-byte reports");
    b=FixtureReply(c,0x1a,1);b[7]^=1;check(!Payload(c,b,0x1a),"Cherry exact battery read header");
    c=find("mchose_g87");b=FixtureReply(c,0x4a,1);b[6]=0x22;p=Payload(c,b,0x4a);check(p&&!DecodeBattery(c,*p),"MCHOSE unknown flags");
    c.output=0;c.layout.bytes.erase({Report::Output,0x13});check(Admit(c),"MCHOSE split reader admission");
    auto writer=c;writer.path="writer";writer.page=0xff04;writer.input=0;writer.output=20;
    writer.layout.bytes.clear();writer.layout.bytes[{Report::Output,0x13}]=20;
    check(bool(SelectMchose(c,{c,writer})),"MCHOSE unique split channels and anchor");
    auto duplicate=c;duplicate.path="another reader";check(!SelectMchose(c,{c,writer,duplicate}),"MCHOSE ambiguous input");
    auto foreign=writer;foreign.physical="another receiver";check(!SelectMchose(c,{c,foreign}),"MCHOSE foreign receiver");
    foreign=writer;foreign.revision^=1;check(!SelectMchose(c,{c,foreign}),"MCHOSE peer revision change");
    foreign=writer;foreign.page=0xff02;check(!SelectMchose(c,{c,foreign}),"MCHOSE missing official anchor collection");
    c=find("aula_f99pro");b=FixtureReply(c,0x4a,1);b[19]^=1;check(!Payload(c,b,0x4a),"FA09 checksum");
    unsigned identities=0;
    Call replaced=[&](unsigned cmd,unsigned seq,bool)->std::optional<Bytes>
    {auto raw=FixtureReply(c,cmd,seq);if(cmd==5&&++identities==3){raw[10]^=1;raw[19]=static_cast<unsigned char>(Sum(raw,0,19));}return Payload(c,raw,cmd,seq);};
    check(!Run(c,replaced,1),"FA09 model changed after battery reads");
    c=find("kzzi_k75v2_input");b=FixtureReply(c,0x0a,1);b[5]=2;b[19]=static_cast<unsigned char>(Sum(b,0,19));check(Invalidates(c,b),"KZZI link event invalidates identity");
    b=FixtureReply(c,0x0a,1);b[7]=0x10;b[19]=static_cast<unsigned char>(Sum(b,0,19));p=Payload(c,b,0x0a);check(p&&!DecodeBattery(c,*p),"KZZI no-current-level notification");
    check(Request(c,0x0a).empty(),"no KZZI notification initialization write");
    for(const char* name:{"weikav_nut75","aula_hub_keyboard"})
    {
        c=find(name);unsigned count=std::min(c.output-9,InfoSize(c));b=FixtureReply(c,0x10,count);
        check(!Payload(c,b,0x10,0),"AA10 stale fragment offset");
        for(unsigned size:{32u,64u})
        {
            c.input=c.output=size+1;c.layout.bytes[{Report::Input,0}]=size;c.layout.bytes[{Report::Output,0}]=size;
            unsigned calls=0;Call chunks=[&](unsigned cmd,unsigned offset,bool)->std::optional<Bytes>
            {++calls;return Payload(c,FixtureReply(c,cmd,offset),cmd,offset);};
            auto r=Run(c,chunks,1);check(r&&r->percentage==66&&calls==2*((InfoSize(c)+size-9)/(size-8)),"complete 32/64-byte AA10 assembly twice");
        }
    }
    c=find("aula_hub_keyboard");b=FixtureInfo(c);b[6]^=1;check(!DecodeBattery(c,b),"AULA exact board identity");
    check(Invalidates(c,{0x55,0xfc,4})&&Invalidates(c,{0x55,0xfc,5})&&Invalidates(c,{0x55,0xfc,6}),"AULA disconnect sleep reset events");
    c=find("vgn_v98pro_v4");b=Request(c,0x87);check(Sum(b,0,64)==255,"V4 command checksum");
    b=FixtureReply(c,0x87,1);b[4]=2;check(!Payload(c,b,0x87),"V4 length header");
    c=find("exx_alpha87");b=FixtureReply(c,0x87,1);b[8]=0x20;p=Payload(c,b,0x87);check(p&&!DecodeBattery(c,*p),"EXX charge state enum");
    c=find("mchose_qhw");b=FixtureReply(c,0x904,0x23);check(!Payload(c,b,0x904,0x24),"QHW stale sequence");
    b=FixtureReply(c,0x904,0x23);b[3]=24;check(!Payload(c,b,0x904,0x23),"QHW payload bounds");
    identities=0;
    Call changedBoard=[&](unsigned cmd,unsigned seq,bool)->std::optional<Bytes>
    {auto raw=FixtureReply(c,cmd,seq);if(cmd==0x900&&++identities==2){raw[10]=0x15;raw[11]=0x10;}return Payload(c,raw,cmd,seq);};
    check(!Run(c,changedBoard,1),"QHW changed downstream board");
    c.input=c.output=64;c.layout.bytes[{Report::Input,0x4d}]=64;c.layout.bytes[{Report::Output,0x4d}]=64;
    check(Admit(c),"QHW 64-byte reports");
    Call qhw64=[&](unsigned cmd,unsigned seq,bool)->std::optional<Bytes>{return Payload(c,FixtureReply(c,cmd,seq),cmd,seq);};
    check(bool(Run(c,qhw64,1)),"QHW 64-byte transaction");
    c=find("atk_a87");c.wired=false;unsigned links=0;
    Call lateDisconnect=[&](unsigned cmd,unsigned seq,bool)->std::optional<Bytes>
    {if(cmd==0xfd&&++links==3)return {};return Payload(c,FixtureReply(c,cmd,seq),cmd,seq);};
    check(!Run(c,lateDisconnect,1)&&links==3,"ATK final disconnect invalidates both reads");
}
}
