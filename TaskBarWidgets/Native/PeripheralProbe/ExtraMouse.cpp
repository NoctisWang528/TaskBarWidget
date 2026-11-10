#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <numeric>
#include <random>
#include <stdexcept>
#include "hidapi.h"
#include "ExtraMouse.h"

namespace peripheral::extra_mouse
{
namespace
{
struct Target
{
    unsigned vid,pid; int mi; unsigned page,usage,revision;
    const char* protocol; const char* product; unsigned route; bool wired;
    uint64_t expectedModel = 0;
};
const Target targets[] = {
#include "ExtraMouseTargets.inc"
};
struct CompxModel { unsigned vid,pid,cid,mid,type; bool ignoreCharge; };
const CompxModel compxModels[] = {
#include "ExtraMouseModels.inc"
};
enum class Mode { Output, Feature, ControlFeature, ControlInput, FeatureThenInput };
bool Prefix(const Bytes& b,std::initializer_list<unsigned char> p)
{return b.size()>=p.size()&&std::equal(p.begin(),p.end(),b.begin());}
unsigned Sum(const Bytes& b,size_t first,size_t last)
{return std::accumulate(b.begin()+first,b.begin()+last,0u)&255;}
unsigned U16(const Bytes& b,unsigned at){return b[at]|(unsigned(b[at+1])<<8);}
Bytes Packet(unsigned size,std::initializer_list<unsigned char> p)
{if(size<p.size())return {};Bytes b(size,0);std::copy(p.begin(),p.end(),b.begin());return b;}
std::string Upper(std::string text)
{for(auto& ch:text)if(ch>='a'&&ch<='z')ch-=32;return text;}
std::string MloongModel(const std::string& product)
{
    auto text=Upper(product);std::string family;unsigned count=0;
    for(const char* value:{"MX300","MX301","MX302","MX306"})if(text.find(value)!=std::string::npos){family=value;++count;}
    if(count!=1)return {};
    if(family=="MX301")
    {
        if(text.find("MX301P2")!=std::string::npos)return "MX301 PRO V2";
        if(text.find("V2")!=std::string::npos)return {};
        if(text.find("MX301P-G")!=std::string::npos)return "MX301 PRO Gold";
        if(text.find("PRO")!=std::string::npos)return text.find("LITE")!=std::string::npos?"MX301 PRO Lite":"MX301 PRO";
        return text.find("LITE")==std::string::npos?family:"";
    }
    return text.find("PRO")==std::string::npos&&text.find("V2")==std::string::npos&&text.find("LITE")==std::string::npos?family:"";
}
unsigned ReportId(const Candidate& c,Report type)
{for(const auto& report:c.layout.bytes)if(report.first.first==type)return report.first.second;return 0;}
bool Match(const Target& t,const Candidate& c)
{
    return t.vid==c.vid&&t.pid==c.pid&&(t.mi<0||t.mi==c.mi)&&(t.page==0xffffffff||t.page==c.page)&&
        (t.usage==0xffffffff||t.usage==c.usage)&&(!t.revision||t.revision==c.revision)&&
        (!*t.product||(t.product[0]=='~'?MloongModel(c.usbProduct)==t.product+1:Upper(c.usbProduct)==Upper(t.product)));
}
const Target* Profile(const Candidate& c)
{for(const auto& t:targets)if(Match(t,c)&&c.adapter==t.protocol)return &t;return nullptr;}
bool Only(const Candidate& c,Report type,unsigned id,unsigned size)
{unsigned count=0;for(const auto& item:c.layout.bytes)if(item.first.first==type)++count;return count==1&&c.layout.Has(type,id,size);}
bool Io(const Candidate& c,unsigned id,unsigned size)
{return c.input==size&&c.output==size&&Only(c,Report::Input,id,size)&&Only(c,Report::Output,id,size);}
bool ZeroIo(const Candidate& c,unsigned size)
{return c.input==size+1&&c.output==size+1&&Only(c,Report::Input,0,size)&&Only(c,Report::Output,0,size);}
bool Feature(const Candidate& c,unsigned id,unsigned size)
{return c.feature==size+(id==0?1:0)&&Only(c,Report::Feature,id,size);}
unsigned Route(const Candidate& c)
{auto p=Profile(c);if(p&&c.adapter!="scyrox_s")return p->route;for(const auto& report:c.layout.bytes)if(report.first.first==Report::Output)return report.first.second;return 0;}
Mode Transfer(const Candidate& c)
{
    const auto& p=c.adapter;
    if(p=="ajazz_159p_legacy")return Mode::ControlFeature;
    if(p=="keychron_status")return Mode::FeatureThenInput;
    if(p=="rapoo_office")return Mode::ControlInput;
    if(p=="eweadn_s9"||p=="valkyrie_m2"||p=="razer_carbon"||p=="valkyrie_m5"||p=="royuan_mouse")return Mode::Feature;
    if(p=="ajazz_159pro"||p=="feature83"||p=="gwolves_feature"||p=="gwolves_legacy"||p=="ajazz_159p_feature"||p=="vaxee"||p=="dareu_a980pro"||p=="sora_v3")return Mode::Feature;
    return Mode::Output;
}
Bytes Request(const Candidate& c,unsigned command,unsigned sequence=0)
{
    const auto& p=c.adapter;unsigned route=Route(c);
    if(p=="ipi_uni")
    {
        Bytes b=command==0xfd?Bytes{8,0xfd,0}:Bytes{8,static_cast<unsigned char>(command),static_cast<unsigned char>(command==1?1:0),0,static_cast<unsigned char>(sequence?1:0)};
        if(command!=0xfd&&sequence)b.push_back(static_cast<unsigned char>(sequence));
        b.push_back(static_cast<unsigned char>(255-Sum(b,0,b.size())));b.resize(c.output,0);return b;
    }
    if(p=="aula_x5")return Packet(64,{0x0b,static_cast<unsigned char>(command)});
    if(p=="valkyrie_m5"||p=="royuan_mouse")return Packet(c.feature,{static_cast<unsigned char>(ReportId(c,Report::Feature)),0xf7});
    if(p=="x23_freq_m8")return Packet(65,{0,0x55,static_cast<unsigned char>(command),0xa5,0x0b,0x2e,1,1,1,0,0});
    if(p=="ajazz_159p")return Packet(33,{0,0x10});
    if(p=="ajazz_159p_feature")return Packet(c.feature,{0,0x10});
    if(p=="ajazz_159p_legacy")return Packet(c.feature,{0x0c,1,0x20,0,static_cast<unsigned char>(sequence%255)});
    if(p=="ajazz_159pro")
    {
        if(command==0xf6)return Packet(65,{0,0xf6,5});
        if(command==0x8f){auto b=Packet(65,{0,0x8f});b[8]=0x70;return b;}
        if(command==0xf7||command==0xfc)return Packet(65,{0,static_cast<unsigned char>(command)});
        return {};
    }
    if(p=="feature83")return Packet(65,{0,0,0,static_cast<unsigned char>(route),2,0,0x83});
    if(p=="gwolves_feature")return Packet(65,{0,0,0,2,2,0,0x83});
    if(p=="gwolves_legacy")return Packet(65,{0,0,2,0x8f,1});
    if(p=="finalmouse")return Packet(64,{static_cast<unsigned char>(route),2,static_cast<unsigned char>(0x80|command),0});
    if(p=="scyrox_s"){auto b=Packet(64,{static_cast<unsigned char>(route),0x2e,0,3});b[63]=static_cast<unsigned char>(Sum(b,0,63));return b;}
    if(p=="atk_hechi"||p=="ausdom_g03")return Packet(64,{0x0b,static_cast<unsigned char>(command)});
    if(p=="atk_nk")return Packet(c.output,{8,4,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0x49});
    if(p=="vaxee")return Packet(64,{0x0e,0xa5,static_cast<unsigned char>(command),1,1});
    if(p=="zynoo_gp1")return Packet(17,{0,0x9a,0,0,8});
    if(p=="dareu_a980pro")return Packet(65,{0x0a,0,3,7,0x80});
    if(p=="sora_v3")return Packet(16,{6,static_cast<unsigned char>(command),0,0,1});
    if(p=="hecate_g3m")
    {auto b=Packet(64,{4,0,0,static_cast<unsigned char>(command),static_cast<unsigned char>(command==0x1a?6:0)});unsigned sum=std::accumulate(b.begin()+3,b.end(),0u);b[1]=static_cast<unsigned char>(sum);b[2]=static_cast<unsigned char>(sum>>8);return b;}
    if(p=="rog_8k")return Packet(c.output,{3,0x12,7,0,0});
    if(p=="vgn_kc2")return Packet(command==3?c.layout.bytes.at({Report::Output,0xb5}):64,{static_cast<unsigned char>(command==3?0xb5:0xb3),static_cast<unsigned char>(command)});
    if(p=="vgn_ld")return Packet(c.output,{0xa0,2,0x25});
    if(p=="waizowl_8k")
    {auto b=Packet(65,{0,static_cast<unsigned char>(command),0,0x81,1});b[64]=static_cast<unsigned char>(0xa1-Sum(b,1,64));return b;}
    if(p=="keychron_status")return Packet(64,{0xb3,6});
    if(p=="nzxt_telemetry")return Packet(64,{0x4e,2,0x81,0,0xb0});
    if(p=="rapoo_office")return Packet(c.output,{0xba,0xa5,0xaa});
    if(p=="aula_sc525")return Packet(16,{0x0e,static_cast<unsigned char>(command)});
    if(p=="xiberia_x7")return Packet(65,{0,0xaa,0x12,0,0});
    if(p=="vgn_dragonfly3_se")return Packet(65,{0,0x55,static_cast<unsigned char>(command)});
    if(p=="vgn_dragonfly3_master"||p=="x23_compx")
    {auto b=Packet(17,{8,static_cast<unsigned char>(command)});if(command==1){b[5]=8;for(unsigned i=0;i<4;++i)b[6+i]=static_cast<unsigned char>(sequence>>(8*i));}b[16]=static_cast<unsigned char>(0x55-Sum(b,0,16));return b;}
    if(p=="corsair_bragi")return Packet(65,{0,static_cast<unsigned char>(8|sequence),2,static_cast<unsigned char>(command)});
    if(p=="eweadn_s9"||p=="valkyrie_m2")
    {
        auto b=Packet(64,{3,0,0x50,0,static_cast<unsigned char>(p=="valkyrie_m2"?(command==0x47?1:2):0),static_cast<unsigned char>(command),static_cast<unsigned char>(sequence)});
        b[1]=static_cast<unsigned char>(Sum(b,2,64));return b;
    }
    if(p=="razer_carbon")
    {auto b=Packet(91,{0,0,static_cast<unsigned char>(sequence%31)});b[6]=2;b[7]=7;b[8]=static_cast<unsigned char>(command);for(unsigned i=3;i<89;++i)b[89]^=b[i];return b;}
    if(p=="mloong_mx301pro"||p=="mloong_mx306")
    {
        if(p=="mloong_mx306")return command==0xed?Packet(64,{0xf0,0xed,1}):Packet(64,{0xf0,0x30,1,0,0x2e});
        return command==0xed?Packet(65,{0,0x55,0xed}):Packet(65,{0,0x55,0x30,0xa5,0x0b,0x2e,1,1,1});
    }
    if(p.rfind("x23_freq_",0)==0)
    {
        if(command==0x52)return Packet(64,{0xf0,0x52});
        if(p=="x23_freq_sc311")return Packet(65,{0,0x55,0x30,0xa5,0x2e,0x2e});
        if(p=="x23_freq_c")return Packet(64,{0xf0,0x30,0xa5,0x0b,0x2e,1,1,1,0,0});
        return Packet(c.output,{static_cast<unsigned char>(ReportId(c,Report::Output)),0x55,0x30,1,0x0b,0x2e,1,1,1,0,0});
    }
    if(p=="aula_v9"||p=="delux_m700")return Packet(64,{4,0x20,0,0x1a,6,0,0,0});
    if(p=="wlmouse_cube")
    {auto b=Packet(64,{4,0,0,0x1a,6,0,0,0});unsigned crc=0xffff;for(unsigned i=3;i<32;++i){crc^=b[i];for(unsigned bit=0;bit<8;++bit)crc=(crc&1)?(crc>>1)^0xa001:crc>>1;}b[1]=static_cast<unsigned char>(crc);b[2]=static_cast<unsigned char>(crc>>8);return b;}
    return {};
}
std::optional<Bytes> Payload(const Candidate& c,Bytes b,unsigned command,unsigned sequence=0,unsigned packageSize=65)
{
    const auto& p=c.adapter;auto q=Request(c,command,sequence);
    if(b==q)return {};
    if(p=="ipi_uni")
    {
        if(b.size()!=c.input||!Prefix(b,{8,static_cast<unsigned char>(command),static_cast<unsigned char>(command==1?1:0)}))return {};
        if(command==0xfd){if(b.size()<14||Sum(b,0,14)!=255||b[12]<10||b[12]>std::min(c.input,c.output))return {};return b;}
        if(b.size()<9||(b[3]!=0&&b[3]!=16))return {};
        unsigned start=b[3]?8:5,length=b[3]?b[7]:b[4];
        unsigned total=b[3]?(b[4]|((b[6]>>4)<<8)):1,current=b[3]?(b[5]|((b[6]&15)<<8)):0;
        if(!total||total>8||current>=total||!length||length>64||start+length>=std::min(c.input,packageSize)||Sum(b,0,start+length+1)!=255)return {};
        return b;
    }
    if(p=="aula_x5")
    {
        unsigned rid=ReportId(c,Report::Input);
        if(b.size()!=64||b[0]!=rid||b[1]!=command||std::equal(b.begin()+1,b.end(),q.begin()+1)||(command!=0xd0&&b[3]))return {};
        return Bytes(b.begin()+4,b.end());
    }
    if(p=="valkyrie_m5"||p=="royuan_mouse")
    {
        if(b.size()!=c.feature||b[0]!=ReportId(c,Report::Feature))return {};b.erase(b.begin());
        if(p=="royuan_mouse")
        {if(b.size()!=64||b[0]||b[3]||b[4]||b[5]||!std::any_of(b.begin(),b.end(),[](unsigned char x){return x!=0;}))return {};}
        else if(b.size()<9||b[0]>1||b[4]||b[5]>1||b[8]||(b[6]!=2&&b[6]!=3))return {};
        if(b[2]>100)return {};return b;
    }
    if(p=="x23_freq_m8")
    {
        if(b.size()==65&&b[0]==0)b.erase(b.begin());
        if(b.size()!=64||!Prefix(b,{0xaa,static_cast<unsigned char>(command),0xa5})||b[4]!=(command==0x30?0x0a:0x2e)||b[5]!=1||b[6]!=1||b[7]!=1)return {};
        return b;
    }
    if(p=="ajazz_159p")
    {
        if(b.size()==33&&b[0]==0)b.erase(b.begin());
        if(b.size()!=32)return {};
        if(Prefix(b,{0x10,0,1,0x0b,'M','1','8','3'}))return b;
        if(c.vid==0x249a&&c.pid==0x5c2f&&c.revision==0x183&&Prefix(b,{0x10,0,1,0x0b,'M','1','7','9'})&&Sum(b,4,31)==b[31])return b;
        if(Prefix(b,{0x10,0,1,0x0b,'M','6','2','0'}))return b;
        return {};
    }
    if(p=="ajazz_159p_feature")
    {if(b.size()!=c.feature||!Prefix(b,{0,0x10,0,1,0x0b,'M','6','2','0'}))return {};return b;}
    if(p=="ajazz_159p_legacy")
    {if(b.size()!=c.feature||!Prefix(b,{0x0c,1,0x20,0})||!std::equal(b.begin()+8,b.begin()+12,Bytes{'M','6','2','0'}.begin()))return {};return b;}
    if(p=="ajazz_159pro")
    {
        if(b.size()!=65||b[0])return {};b.erase(b.begin());
        if(command==0xf7)
        {if(b[0]||b[3]||b[4]||b[5]||b[2]>100||!std::any_of(b.begin(),b.begin()+6,[](unsigned char x){return x!=0;}))return {};}
        else if(command==0xfc){if(b[0]!=0x8f)return {};}
        return b;
    }
    if(p=="feature83"||p=="gwolves_feature"||p=="gwolves_legacy")
    {
        if(b.size()!=65||b[0])return {};b.erase(b.begin());
        if(p=="gwolves_legacy"){if(!Prefix(b,{0xa1,2,0x8f}))return {};return b;}
        if(p=="feature83")
        {
            if((b[0]==0xa0||b[0]==0xa2)&&c.vid==0x36a7&&b[3]==2&&b[5]==0x83)return b;
            if(b[0]!=0xa1||b[3]!=2||b[5]!=0x83)return {};
        }
        else if(b[0]!=0xa1||b[2]!=2||b[3]!=2||b[5]!=0x83)return {};
        return b;
    }
    if(p=="finalmouse")
    {unsigned length=command==5?2:1;if(b.size()<4+length||b.size()>64||b[0]!=(Route(c)==4?5:2)||b[1]!=length+2||b[2]!=command||b[3]!=length)return {};return Bytes(b.begin()+4,b.begin()+4+length);}
    if(p=="scyrox_s")
    {if(b.size()!=64||b[0]!=Route(c)||b[1]!=0x2e||b[62]==1)return {};return b;}
    if(p=="atk_hechi"||p=="ausdom_g03")
    {if(b.size()!=64||!Prefix(b,{0x0b,static_cast<unsigned char>(command)})||b[3])return {};return b;}
    if(p=="atk_nk")
    {if(b.size()!=c.input||b.size()<17||!Prefix(b,{8,4}))return {};b.resize(17);Candidate compx=c;compx.protocol=Protocol::Compx;if(!peripheral::Decode(compx,b))return {};return b;}
    if(p=="vaxee")
    {if(b.size()<6||b.size()>64||!Prefix(b,{0x0e,0xa5,static_cast<unsigned char>(command)})||b[4]!=1||std::equal(b.begin(),b.end(),q.begin()))return {};return b;}
    if(p=="zynoo_gp1")
    {if(b.size()==17&&b[0]==0)b.erase(b.begin());if(b.size()!=16||!Prefix(b,{0x9a,0,0})||std::equal(b.begin(),b.end(),q.begin()+1))return {};return b;}
    if(p=="dareu_a980pro")
    {if(b.size()!=65||b[0]!=0x0a||(b[1]&0xf0)||(b[1]&0xf)!=2||b[3]!=7||b[4]!=0x80)return {};return b;}
    if(p=="sora_v3")
    {if((b.size()!=16&&!(c.pid==0xe010&&b.size()==9))||b[0]!=6||b[1]!=command||std::equal(b.begin(),b.end(),q.begin()))return {};return b;}
    if(p=="hecate_g3m")
    {if(b.size()!=64||b[0]!=4||b[3]!=command||b[7]>=0xfe)return {};if(command==0xaa){if(b[8]==0xff)return {};return b;}if(b[4]!=6||b[5]||b[6])return {};return b;}
    if(p=="rog_8k")
    {if(b.size()!=c.input||!Prefix(b,{3,0x12,7,0,0}))return {};return b;}
    if(p=="vgn_kc2")
    {if(b.size()<2)return {};auto declared=c.layout.bytes.find({Report::Input,b[0]});if(declared==c.layout.bytes.end()||(declared->second!=64&&!(command==3&&declared->second==21))||(b.size()!=declared->second&&b.size()!=64)||b[1]!=command)return {};b.resize(declared->second);if(std::all_of(b.begin()+2,b.end(),[](unsigned char x){return x==0;}))return {};return b;}
    if(p=="vgn_ld")
    {if(b.size()<4||!c.layout.Has(Report::Input,b[0],unsigned(b.size()))||b[1]!=2||b[2]!=0x25||!b[3])return {};return b;}
    if(p=="waizowl_8k")
    {if(b.size()==65&&b[0]==0)b.erase(b.begin());if(b.size()!=64||b[0]!=command||(b[3]!=1&&!(command==0x41&&b[3]==3))||std::equal(b.begin(),b.end(),q.begin()+1))return {};return b;}
    if(p=="keychron_status")
    {if(b.size()!=64||!Prefix(b,{0xb4,6}))return {};return b;}
    if(p=="nzxt_telemetry")
    {if(b.size()!=64||!Prefix(b,{0x4e,2,0x97}))return {};return b;}
    if(p=="rapoo_office")
    {if(b.size()<3||b.size()>32||b[0]!=1)return {};return b;}
    if(p=="aula_sc525")
    {if(b.size()!=32||!Prefix(b,{0x0e,static_cast<unsigned char>(command)}))return {};return b;}
    if(p=="xiberia_x7")
    {if(b.size()==65&&b[0]==0)b.erase(b.begin());if(b.size()!=64||!Prefix(b,{0x55,0x12})||b[2]<5||b[2]>60)return {};return b;}
    if(p=="vgn_dragonfly3_se")
    {if(b.size()==65&&b[0]==0)b.erase(b.begin());if(b.size()!=64||b[1]!=command||std::equal(b.begin(),b.end(),q.begin()+1))return {};return b;}
    if(p=="vgn_dragonfly3_master"||p=="x23_compx")
    {
        if(b.size()!=17||!Prefix(b,{8,static_cast<unsigned char>(command),0}))return {};
        if(p=="x23_compx"&&(Sum(b,0,17)!=0x55||!b[5]||b[5]>10||(command==1&&b[5]<7)||(command==4&&b[5]<2)))return {};
        return b;
    }
    if(p=="corsair_bragi")
    {if(b.size()==65&&b[0]==0)b.erase(b.begin());if(b.size()!=64||b[0]!=sequence||b[1]!=2||b[2])return {};return Bytes(b.begin()+3,b.begin()+6);}
    if(p=="eweadn_s9"||p=="valkyrie_m2")
    {if(b.size()!=64||!Prefix(b,{3,0x50,0})||b[4]!=q[4]||b[5]!=sequence||!b[3]||b[3]>10)return {};return Bytes(b.begin()+6,b.begin()+6+b[3]);}
    if(p=="razer_carbon")
    {if(b.size()!=91||b[0]||b[1]!=2||b[2]!=q[2]||!std::equal(b.begin()+3,b.begin()+9,q.begin()+3)||b[9])return {};unsigned crc=0;for(unsigned i=3;i<89;++i)crc^=b[i];if(crc!=b[89])return {};return b;}
    if(p=="mloong_mx301pro"||p=="mloong_mx306")
    {
        if(b.size()==65&&b[0]==0)b.erase(b.begin());
        if(b.size()!=64)return {};unsigned offset=0,count=0;
        if(p=="mloong_mx306"){if(b[0]!=0xf0||b[1]!=command)return {};count=1;}
        else for(unsigned i=0;i<2;++i)if((b[i]==0x55||b[i]==0xaa)&&b[i+1]==command){offset=i;++count;}
        if(count!=1)return {};
        auto query=Request(c,command);if(p=="mloong_mx301pro"&&std::equal(b.begin(),b.end(),query.begin()+1))return {};
        if(command==0xed){if(b[offset+8])return {};return Bytes{1};}
        if(b[offset+9]>1||(p=="mloong_mx306"&&!b[offset+8]))return {};
        return Bytes{b[offset+8],b[offset+9]};
    }
    if(p.rfind("x23_freq_",0)==0)
    {
        if(command==0x52){if(b.size()!=64||!Prefix(b,{0xf0,0x52}))return {};return b;}
        unsigned rid=ReportId(c,Report::Input);auto declared=c.layout.bytes.find({Report::Input,rid});if(declared==c.layout.bytes.end())return {};
        if(rid){if(b.size()!=declared->second||b[0]!=rid)return {};b.erase(b.begin());}
        else {if(b.size()==declared->second+1&&b[0]==0)b.erase(b.begin());if(b.size()!=declared->second)return {};if(q.size()==b.size()+1&&std::equal(b.begin(),b.end(),q.begin()+1))return {};}
        if(p=="x23_freq_c"){if(rid!=0xf0||b.size()<9||b[0]!=0x30||b[8]>1)return {};return Bytes{b[7],b[8]};}
        unsigned offset=0,count=0;for(unsigned i=0;i<2;++i)if(b.size()>=i+10&&(b[i]==0xaa||b[i]==0xf0)&&b[i+1]==0x30){offset=i;++count;}
        if(count!=1||b[offset+9]>1)return {};return Bytes{b[offset+8],b[offset+9]};
    }
    if(p=="aula_v9"||p=="delux_m700"||p=="wlmouse_cube")
    {if(b.size()!=64||b[0]!=4||b[3]!=0x1a)return {};if(p=="wlmouse_cube"&&(b[4]!=6))return {};return b;}
    return {};
}
std::optional<Reading> Battery(const Candidate& c,const Bytes& b)
{
    Reading r;unsigned value=255;const auto& p=c.adapter;
    if(p=="ipi_uni")
    {if(b.size()!=4||b[1]!=1||b[3]>1)return {};value=b[2]&127;}
    else if(p=="aula_x5")
    {if(b.size()<16||b[13]>1||b[15]>1)return {};value=b[14];r.charging=b[13]==1;}
    else if(p=="valkyrie_m5"||p=="royuan_mouse")value=b[2];
    else if(p=="x23_freq_m8")value=b[8];
    else if(p=="ajazz_159p")
    {r.peer=std::string(b.begin()+4,b.begin()+8);if(r.peer=="M620")value=b[13];else{if(b[12]>1||b[14]!=1)return {};value=b[13];r.charging=b[12]==1;}}
    else if(p=="ajazz_159p_feature"){value=b[14];r.peer="M620";}
    else if(p=="ajazz_159p_legacy"){value=b[18];r.peer="M620";}
    else if(p=="ajazz_159pro")value=b[2];
    else if(p=="feature83")
    {if(b[0]==0xa0){r.state="sleeping";return r;}if(b[6]>1)return {};value=b[7];r.charging=b[6]==1;}
    else if(p=="gwolves_feature"){if(b[6]>1)return {};value=b[7];r.charging=b[6]==1;}
    else if(p=="gwolves_legacy"){if(b[4]>1)return {};value=b[5];r.charging=b[4]==1;}
    else if(p=="scyrox_s")
    {if(b[5]>1||b[6]>1)return {};if(!b[6]){r.state="sleeping";return r;}unsigned vid=U16(b,12),pid=U16(b,14);if(vid!=0x395a||(pid!=3&&pid!=5&&pid!=7&&pid!=9&&pid!=0xb&&pid!=0xd))return {};r.peer=std::to_string(vid)+":"+std::to_string(pid);value=b[4];r.charging=b[5]==1;}
    else if(p=="atk_hechi"||p=="ausdom_g03")
    {if(p=="ausdom_g03"&&b[4]>=8)return {};value=b[18];r.charging=b[17]==1&&(p!="ausdom_g03"||(b[20]!=1&&value<100));r.peer=std::to_string(b[15])+":"+std::to_string(b[16]);}
    else if(p=="atk_nk"){Candidate compx=c;compx.protocol=Protocol::Compx;return peripheral::Decode(compx,b);}
    else if(p=="vaxee"){if(b[5]>20)return {};value=b[5]*5;r.estimated=true;r.levelText=std::to_string(b[5])+"/20";}
    else if(p=="zynoo_gp1"){if(!b[11]){r.state="sleeping";return r;}value=b[10]&127;r.charging=bool(b[10]&128);}
    else if(p=="dareu_a980pro"){if(b[7]>2||!b[8])return {};value=b[8];r.charging=b[7]==1||b[7]==2;r.estimated=true;}
    else if(p=="hecate_g3m"){if(b[9]>2)return {};value=b[8];r.charging=b[9]==1;}
    else if(p=="rog_8k"){if(b[10]>1)return {};if(!b[5]){r.state="sleeping";return r;}value=b[5];r.charging=b[10]==1;}
    else if(p=="vgn_kc2")value=b[20]&127;
    else if(p=="vgn_ld")value=b[3];
    else if(p=="waizowl_8k")value=b[6];
    else if(p=="keychron_status")value=b[20];
    else if(p=="nzxt_telemetry"){value=U16(b,17);r.charging=bool(b[22]&1);}
    else if(p=="rapoo_office")value=b[2];
    else if(p=="aula_sc525"){if(!(b[9]&0x80)){r.state="sleeping";return r;}value=b[8]&127;r.charging=bool(b[8]&128);}
    else if(p=="xiberia_x7"){if(b[4]>1||b[8]!=1||(b[7]&15)!=1)return {};value=b[5];r.charging=b[4]==1;}
    else if(p=="vgn_dragonfly3_se"){if(b[9])return {};value=b[8];}
    else if(p=="vgn_dragonfly3_master"||p=="x23_compx"){if(b[7]>1)return {};value=b[6];r.charging=b[7]==1;}
    else if(p=="eweadn_s9"||p=="valkyrie_m2"){if(b.size()<7)return {};value=b[0]&127;r.charging=bool(b[0]&128);}
    else if(p=="mloong_mx301pro"||p=="mloong_mx306"||p.rfind("x23_freq_",0)==0){if(b.size()!=2||b[1]>1)return {};value=b[0];r.charging=b[1]==1;}
    else if(p=="aula_v9"||p=="wlmouse_cube")value=b[8];
    else if(p=="delux_m700")
    {if((b[4]!=0&&b[4]!=6)||b[5]||b[6]||b[7]==0xff||b[7]==0xfe||b[9]||!std::any_of(b.begin()+8,b.begin()+14,[](unsigned char x){return x!=0;}))return {};value=b[8];}
    if(value>100)return {};r.percentage=int(value);return r;
}
struct Session
{
    const Candidate& c;const std::function<void()>& progress;hid_device* h=nullptr;ULONGLONG deadline;unsigned packageSize=65;
    Session(const Candidate& candidate,const std::function<void()>& report):c(candidate),progress(report),deadline(GetTickCount64()+12000)
    {
        Candidate admitted=c;if(!Admit(admitted)||admitted.adapter!=c.adapter)return;
        if(progress)progress();h=hid_open_path(c.path.c_str());if(!h)return;
        if(progress)progress();auto info=hid_get_device_info(h);
        bool valid=info&&info->vendor_id==c.vid&&info->product_id==c.pid&&info->release_number==c.revision&&
            info->usage_page==c.page&&info->usage==c.usage&&info->interface_number==c.mi;
        unsigned char descriptor[4096];if(progress)progress();int count=hid_get_report_descriptor(h,descriptor,sizeof(descriptor));
        auto layout=count>0?ParseDescriptor(Bytes(descriptor,descriptor+count)):Layout{};
        valid=valid&&layout.valid&&layout.bytes==c.layout.bytes;
        for(bool serial:{true,false})
        {
            const auto& expected=serial?c.serial:c.usbProduct;if(expected.empty())continue;
            wchar_t value[256]{};if(progress)progress();int result=serial?hid_get_serial_number_string(h,value,256):hid_get_product_string(h,value,256);
            int size=result==0?WideCharToMultiByte(CP_UTF8,0,value,-1,nullptr,0,nullptr,nullptr):0;
            std::string actual(size>0?size:0,'\0');if(size>0){WideCharToMultiByte(CP_UTF8,0,value,-1,actual.data(),size,nullptr,nullptr);actual.pop_back();}
            valid=valid&&result==0&&actual==expected;
        }
        if(!valid){hid_close(h);h=nullptr;}
    }
    ~Session(){if(h)hid_close(h);}
    bool Send(const Bytes& q,Mode mode)
    {
        if(!h||q.empty()||GetTickCount64()>=deadline)return false;if(progress)progress();
        int n=mode==Mode::Output||mode==Mode::ControlInput?hid_write(h,q.data(),q.size()):hid_send_feature_report(h,q.data(),q.size());return n==int(q.size());
    }
    std::optional<Bytes> Exchange(unsigned command,unsigned sequence=0)
    {
        auto q=Request(c,command,sequence);Mode mode=Transfer(c);
        if(!h||q.empty()||GetTickCount64()>=deadline)return {};
        if(mode==Mode::Output||mode==Mode::FeatureThenInput)
        {
            if(progress)progress();unsigned char discard[4096];bool drained=false;
            for(unsigned i=0;i<32;++i){int n=hid_read_timeout(h,discard,sizeof(discard),1);if(n<0)return {};if(!n){drained=true;break;}}
            if(!drained)return {};
        }
        if(mode!=Mode::ControlFeature&&!Send(q,mode))return {};
        if(mode==Mode::ControlInput)Sleep(110);
        ULONGLONG until=std::min(deadline,GetTickCount64()+1000);
        while(GetTickCount64()<until)
        {
            Bytes b(std::max({c.input,c.feature,65u}));int n=0;
            if(progress)progress();
            if(mode==Mode::Feature||mode==Mode::ControlFeature)
            {
                Sleep(20);b=mode==Mode::ControlFeature?q:Bytes(q.size(),0);b[0]=q[0];n=hid_get_feature_report(h,b.data(),b.size());
            }
            else if(mode==Mode::ControlInput){b.assign(32,0);b[0]=0xba;n=hid_get_input_report(h,b.data(),b.size());if(n>0&&b[0]==2){Sleep(2);continue;}}
            else n=hid_read_timeout(h,b.data(),b.size(),40);
            if(n<0)return {};if(!n)continue;b.resize(n);if(auto result=Payload(c,std::move(b),command,sequence,packageSize))return result;
        }
        return {};
    }
};
}
bool KnownIdentity(unsigned vid,unsigned pid)
{for(const auto& t:targets)if(t.vid==vid&&t.pid==pid)return true;return false;}
bool Admit(Candidate& c)
{
    const Target* chosen=nullptr;
    for(const auto& t:targets)
    {
        if(!Match(t,c))continue;std::string p=t.protocol;bool valid=false;
        if(p=="ipi_uni")valid=c.input>=15&&c.input<=65&&c.output>=15&&c.output<=65&&Only(c,Report::Input,8,c.input)&&Only(c,Report::Output,8,c.output);
        else if(p=="aula_x5")valid=c.input==64&&c.output==64&&(Only(c,Report::Input,10,64)||Only(c,Report::Input,11,64))&&Only(c,Report::Output,11,64);
        else if(p=="valkyrie_m5")valid=c.feature>=10&&c.feature<=65&&Feature(c,ReportId(c,Report::Feature),c.feature-(ReportId(c,Report::Feature)==0?1:0));
        else if(p=="royuan_mouse")valid=Feature(c,0,64);
        else if(p=="x23_freq_m8")valid=ZeroIo(c,64);
        else if(p=="ajazz_159p")valid=ZeroIo(c,32)&&!c.feature;
        else if(p=="ajazz_159p_feature")valid=Feature(c,0,32)||Feature(c,0,64);
        else if(p=="ajazz_159p_legacy")valid=Feature(c,0x0c,33)||Feature(c,0x0c,64);
        else if(p=="ajazz_159pro"||p=="feature83"||p=="gwolves_feature"||p=="gwolves_legacy")valid=c.page>=0xff00&&Feature(c,0,64);
        else if(p=="finalmouse")valid=c.input==64&&c.output==64&&Only(c,Report::Input,t.route==4?5:2,64)&&Only(c,Report::Output,t.route,64);
        else if(p=="scyrox_s")
        {for(const auto& report:c.layout.bytes)if(report.first.first==Report::Output&&report.first.second)valid=Io(c,report.first.second,64);}
        else if(p=="atk_hechi"||p=="ausdom_g03")valid=Io(c,0x0b,64);
        else if(p=="atk_nk")valid=(c.input==17||c.input==64)&&(c.output==17||c.output==64)&&Only(c,Report::Input,8,c.input)&&Only(c,Report::Output,8,c.output);
        else if(p=="vaxee")valid=Feature(c,0x0e,64);
        else if(p=="zynoo_gp1")valid=ZeroIo(c,16);
        else if(p=="dareu_a980pro")valid=Feature(c,0x0a,65);
        else if(p=="sora_v3")valid=Feature(c,6,16)||(c.pid==0xe010&&Feature(c,6,9));
        else if(p=="hecate_g3m")valid=Io(c,4,64)&&!c.feature;
        else if(p=="rog_8k")valid=c.input>=21&&c.input<=65&&c.output>=21&&c.output<=65&&Only(c,Report::Input,3,c.input)&&Only(c,Report::Output,3,c.output);
        else if(p=="vgn_kc2")
        {valid=c.input==64&&c.output==64&&c.layout.Has(Report::Output,0xb3,64)&&(c.layout.Has(Report::Output,0xb5,21)||c.layout.Has(Report::Output,0xb5,64));for(const auto& report:c.layout.bytes)if(report.first.first==Report::Input&&(report.second!=21&&report.second!=64))valid=false;}
        else if(p=="vgn_ld")
        {valid=(c.output==33||c.output==64)&&Only(c,Report::Output,0xa0,c.output)&&c.input>=4&&c.input<=65;}
        else if(p=="waizowl_8k")valid=ZeroIo(c,64);
        else if(p=="keychron_status")valid=c.input==64&&c.feature==64&&Only(c,Report::Input,0xb4,64)&&Only(c,Report::Feature,0xb3,64);
        else if(p=="nzxt_telemetry")valid=Io(c,0x4e,64);
        else if(p=="rapoo_office")
        {auto output=c.layout.bytes.find({Report::Output,0xba});valid=c.output>=3&&c.output<=65&&output!=c.layout.bytes.end()&&output->second>=3&&output->second<=c.output;}
        else if(p=="aula_sc525")valid=c.input==32&&c.output==16&&Only(c,Report::Input,0x0e,32)&&Only(c,Report::Output,0x0e,16);
        else if(p=="xiberia_x7"||p=="vgn_dragonfly3_se"||p=="corsair_bragi")valid=ZeroIo(c,64);
        else if(p=="vgn_dragonfly3_master"||p=="x23_compx")valid=Io(c,8,17);
        else if(p=="eweadn_s9"||p=="valkyrie_m2")valid=c.page>=0xff00&&Feature(c,3,64);
        else if(p=="razer_carbon")valid=Feature(c,0,90);
        else if(p=="mloong_mx301pro")valid=ZeroIo(c,64);
        else if(p=="mloong_mx306")valid=Io(c,0xf0,64);
        else if(p.rfind("x23_freq_",0)==0)
        {
            unsigned input=ReportId(c,Report::Input),output=ReportId(c,Report::Output);
            auto ir=c.layout.bytes.find({Report::Input,input}),o=c.layout.bytes.find({Report::Output,output});
            valid=ir!=c.layout.bytes.end()&&o!=c.layout.bytes.end()&&ir->second>=10&&ir->second<=65&&o->second>=10&&o->second<=65&&
                Only(c,Report::Input,input,ir->second)&&Only(c,Report::Output,output,o->second)&&c.input==ir->second+(input==0?1:0)&&c.output==o->second+(output==0?1:0);
            if(p=="x23_freq_c")valid=valid&&Io(c,0xf0,64);
            if(p=="x23_freq_sc311")valid=valid&&ZeroIo(c,64);
        }
        else if(p=="aula_v9"||p=="delux_m700"||p=="wlmouse_cube")valid=Io(c,4,64);
        if(!valid)continue;
        if(chosen&&(std::string(chosen->protocol)!=p||chosen->route!=t.route))return false;
        chosen=&t;
    }
    if(!chosen)return false;c.protocol=Protocol::ExtraMouse;c.adapter=chosen->protocol;c.kind="mouse";c.wired=chosen->wired;
    c.name=c.usbProduct.empty()?"Wireless mouse / receiver":c.usbProduct;return true;
}
std::optional<Reading> Read(const Candidate& c,const std::function<void()>& progress)
{
    Candidate selected=c;Session s(selected,progress);if(!s.h)return {};const auto& p=selected.adapter;
    if(p=="ipi_uni")
    {
        auto initial=s.Exchange(0xfd);if(!initial)return {};s.packageSize=(*initial)[12];
        auto readPayload=[&](unsigned command,unsigned maximum)->std::optional<Bytes>
        {
            Bytes result;unsigned count=1;
            for(unsigned index=0;index<count;++index)
            {
                auto frame=s.Exchange(command,index);if(!frame)return {};
                const auto& b=*frame;unsigned total=b[3]?(b[4]|((b[6]>>4)<<8)):1,current=b[3]?(b[5]|((b[6]&15)<<8)):0;
                if(current!=index||(index&&total!=count))return {};count=total;
                unsigned start=b[3]?8:5,length=b[3]?b[7]:b[4];result.insert(result.end(),b.begin()+start,b.begin()+start+length);
                if(result.size()>maximum)return {};
            }
            return result;
        };
        auto identify=[&]()->bool
        {auto b=readPayload(1,64);return b&&b->size()>=10&&std::equal(b->begin()+4,b->begin()+10,Bytes{0x23,0,0,0,0,2}.begin());};
        if(!identify())return {};std::optional<Reading> result;
        for(unsigned i=0;i<2;++i){auto b=readPayload(2,4);if(!b)return {};result=Battery(c,*b);if(!result)return {};}
        return identify()?result:std::nullopt;
    }
    if(p=="x23_freq_dual")
    {
        bool schemeC=false;
        if(Io(c,0xf0,64)){auto name=s.Exchange(0x52);if(name&&(*name)[9]){schemeC=true;for(unsigned i=9;i<33&&(*name)[i];++i)if((*name)[i]<32||(*name)[i]>126)schemeC=false;}}
        selected.adapter=schemeC?"x23_freq_c":"x23_freq_a";
    }
    std::optional<Bytes> pairedModel;
    auto identify=[&]()->bool
    {
        if(p=="atk_hechi"||p=="ausdom_g03"){auto b=s.Exchange(0x14);return b&&(*b)[4]!=0;}
        if(p=="hecate_g3m")return bool(s.Exchange(0xaa));
        if(p=="aula_x5")
        {
            auto online=s.Exchange(0xd0),model=s.Exchange(0xa0);if(!online||!model||(*online)[0]!=1)return false;
            static const unsigned allowed[]={0x28f7,0x28fb,0x2ee0,0x28ff,0x28e7,0x2ef4,0x2efe,0x2ef6,0x2eee,0x2ef2,0x2f14,0x28e8,0x2f08,0x2f0c,0x2f10};
            if(U16(*model,0)!=0x38a6||std::find(std::begin(allowed),std::end(allowed),U16(*model,2))==std::end(allowed))return false;
            Bytes identity(model->begin(),model->begin()+4);if(pairedModel&&*pairedModel!=identity)return false;pairedModel=identity;return true;
        }
        if(p=="x23_freq_m8")
        {
            auto b=s.Exchange(0x0e);if(!b||(*b)[11]<1||(*b)[11]>6||(*b)[12]<1||(*b)[12]>(*b)[11])return false;
            for(unsigned i=0;i<(*b)[11];++i){unsigned value=U16(*b,13+2*i);if(value<50||value>50000)return false;}return true;
        }
        if(p=="vgn_kc2"){auto b=s.Exchange(3);return b&&(*b)[7]==1;}
        if(p=="waizowl_8k")
        {auto b=s.Exchange(1);if(!b||!((*b)[4]&8))return false;Bytes model(b->begin()+11,b->begin()+15);if(pairedModel&&*pairedModel!=model)return false;static const Bytes allowed[]={{0xf2,7,0,0},{0xf4,7,0,0},{0xf6,7,0,0},{0xf3,7,0,0},{0xf5,7,0,0},{0xb0,7,0,0}};if(std::find(std::begin(allowed),std::end(allowed),model)==std::end(allowed))return false;pairedModel=model;return true;}
        if(p=="sora_v3"&&c.pid==0xe010){auto b=s.Exchange(0x13);return b&&(*b)[8]==1;}
        if(p=="vgn_dragonfly3_master"){auto b=s.Exchange(3);return b&&(*b)[6]==1;}
        if(p=="vgn_dragonfly3_se"){auto b=s.Exchange(0xed);return b&&(*b)[8]>1&&(*b)[8]!=0xff;}
        if(p=="mloong_mx301pro"||p=="mloong_mx306")return bool(s.Exchange(0xed));
        if(p=="aula_sc525")
        {auto b=s.Exchange(0x2d);if(!b)return false;Bytes model(b->begin()+8,b->begin()+12);if(std::all_of(model.begin(),model.end(),[](unsigned char x){return x==0;})||std::all_of(model.begin(),model.end(),[](unsigned char x){return x==255;})||(pairedModel&&*pairedModel!=model))return false;pairedModel=model;return true;}
        if(p=="x23_compx")
        {
            std::random_device random;auto b=s.Exchange(1,random());if(!b)return false;
            Bytes model(b->begin()+10,b->begin()+13);unsigned matches=0;
            for(const auto& known:compxModels)if(known.vid==c.vid&&known.pid==c.pid&&known.cid==model[0]&&known.mid==model[1]&&known.type==model[2])++matches;
            if(matches!=1||(pairedModel&&*pairedModel!=model))return false;pairedModel=model;
            auto online=s.Exchange(3);return online&&(*online)[6]==1;
        }
        if(p=="eweadn_s9"||p=="valkyrie_m2")
        {
            if(!c.wired){auto status=s.Exchange(0x47,0);if(!status||status->size()!=1||(*status)[0]!=1)return false;}
            auto model=s.Exchange(0x4f,0x80);auto profile=Profile(c);if(!model||model->size()<8||!profile||!profile->expectedModel)return false;
            uint64_t uuid=0;for(unsigned i=2;i<8;++i)uuid=(uuid<<8)|(*model)[i];return uuid==profile->expectedModel;
        }
        if(p=="ajazz_159pro")
        {
            if(!s.Exchange(0xf7)||!s.Send(Request(c,0xf6),Mode::Feature))return false;
            auto ready=[&](unsigned index){for(unsigned i=0;i<5;++i){auto b=s.Exchange(0xf7);if(!b)return false;if((*b)[index]!=1)return true;Sleep(100);}return false;};
            if(!ready(5)||!s.Send(Request(c,0x8f),Mode::Feature)||!ready(0))return false;
            auto b=s.Exchange(0xfc);return b&&(*b)[0]==0x8f&&U16(*b,1)==1754&&(*b)[3]==0&&(*b)[4]==0;
        }
        return true;
    };
    if(!identify())return {};
    if(p=="corsair_bragi")
    {
        auto property=[&](unsigned child,unsigned key)->std::optional<unsigned>{auto b=s.Exchange(key,child);if(!b)return {};return (*b)[0]|(unsigned((*b)[1])<<8)|(unsigned((*b)[2])<<16);};
        auto present=property(0,0x36);if(!present||*present>255||!(*present&0xfe))return {};
        unsigned selected=0;for(unsigned slot=1;slot<=7;++slot)if(*present&(1u<<slot))
        {auto vid=property(slot,0x11),pid=property(slot,0x12);if(vid==0x1b1c&&pid==0x1b4c){if(selected)return {};selected=slot;}}
        if(!selected)return {};auto a=property(selected,0x0f),b=property(selected,0x0f);
        if(!a||!b||a!=b||*b>1000||property(selected,0x11)!=0x1b1c||property(selected,0x12)!=0x1b4c||property(0,0x36)!=present)return {};
        Reading r;r.percentage=int(*b/10);return r;
    }
    if(p=="ajazz_159p_legacy"&&!s.Send(Request(c,0,0),Mode::Feature))return {};
    std::optional<Reading> result;
    for(unsigned round=0;round<2;++round)
    {
        if(p=="eweadn_s9"||p=="valkyrie_m2")
        {auto b=s.Exchange(0x4f,0x81);if(!b)return {};result=Battery(c,*b);if(!result)return {};continue;}
        if(p=="razer_carbon")
        {
            std::random_device random;auto charge=s.Exchange(0x84,random()%31);if(!charge||(*charge)[10]>1)return {};
            auto b=s.Exchange(0x80,random()%31);if(!b||(*b)[10]==255)return {};
            Reading r;r.percentage=((*b)[10]*100+127)/255;r.charging=(*charge)[10]==1;result=r;continue;
        }
        if(p=="sora_v3")
        {auto b=s.Exchange(0x12);if(!b||(*b)[8]>100)return {};Reading r;r.percentage=(*b)[8];auto charge=s.Exchange(0x11);if(charge&&(*charge)[8]<=1)r.charging=(*charge)[8]==1;result=r;continue;}
        if(p=="finalmouse")
        {
            if(!c.wired){auto online=s.Exchange(0x24);if(!online||(*online)[0]!=1)return {};}
            auto b=s.Exchange(0x26);Reading r;
            if(b){if((*b)[0]>100)return {};r.percentage=(*b)[0];}
            else if(Route(c)==4)
            {
                b=s.Exchange(5);if(!b)return {};unsigned mv=U16(*b,0);if(mv<2500||mv>4600)return {};
                static const std::pair<int,int> knots[]={{3000,0},{3620,5},{3660,10},{3740,25},{3880,50},{4170,75},{4380,100}};
                int percent=mv>=4380?100:0;for(unsigned i=1;i<std::size(knots);++i)if(mv>=knots[i-1].first&&mv<knots[i].first)percent=knots[i-1].second+(int(mv)-knots[i-1].first)*(knots[i].second-knots[i-1].second)/(knots[i].first-knots[i-1].first);
                r.percentage=percent;r.estimated=true;
            }
            else return {};
            auto charge=s.Exchange(0x25);if(charge&&(*charge)[0]<=1)r.charging=(*charge)[0]==1;
            result=r;continue;
        }
        unsigned command=p=="aula_x5"?0x81:p=="x23_freq_m8"?0x30:p=="atk_hechi"||p=="ausdom_g03"?0x13:p=="ajazz_159pro"?0xf7:p=="vaxee"?0x0b:p=="vgn_kc2"?6:p=="waizowl_8k"?0x41:p=="hecate_g3m"?0x1a:p=="aula_sc525"?0x20:p=="vgn_dragonfly3_se"?0x30:p=="vgn_dragonfly3_master"||p=="x23_compx"?4:0;
        auto b=s.Exchange(command,round+1);if(!b)return {};auto next=Battery(c,*b);if(!next)return {};
        if(p=="vaxee"){auto charge=s.Exchange(0x10);if(charge&&(*charge)[5]<=1)next->charging=(*charge)[5]==1;}
        if(p=="x23_compx")for(const auto& known:compxModels)if(known.vid==c.vid&&known.pid==c.pid&&known.ignoreCharge)next->charging.reset();
        if(result&&result->peer!=next->peer)return {};result=std::move(next);
    }
    if(!identify())return {};
    if(p=="finalmouse"&&!c.wired){auto online=s.Exchange(0x24);if(!online||(*online)[0]!=1)return {};}
    return result;
}
void OfflineSelfTests()
{
    auto check=[](bool value,const char* message){if(!value)throw std::runtime_error(message);};
    Candidate c;c.adapter="ajazz_159p";auto b=Packet(32,{0x10,0,1,0x0b,'M','1','8','3'});b[13]=55;b[14]=1;
    auto p=Payload(c,b,0);check(p&&Battery(c,*p)->percentage==55,"AJAZZ M183 framed battery");b[14]=0;p=Payload(c,b,0);check(p&&!Battery(c,*p),"AJAZZ M183 asleep rejects cached battery");
    c.adapter="aula_v9";b=Packet(64,{4,0,0,0x1a});b[8]=50;check(bool(Payload(c,b,0)),"AULA V9 reply header");b[3]=0;check(!Payload(c,b,0),"AULA V9 wrong command");
    for(const char* family:{"ajazz_159p","ajazz_159p_feature","ajazz_159p_legacy","ajazz_159pro","feature83","gwolves_feature","gwolves_legacy","finalmouse","scyrox_s","atk_hechi","ausdom_g03","atk_nk","aula_v9","delux_m700","wlmouse_cube"})
    {c.adapter=family;c.feature=65;c.input=c.output=64;for(unsigned n=0;n<130;++n)check(!Payload(c,Bytes(n,0xff),0),"Mouse malformed frame");}
}
}
