#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <setupapi.h>
#include <hidsdi.h>
#include <hidpi.h>
#include <objbase.h>
#include <devpkey.h>
#include <algorithm>
#include <chrono>
#include <cstring>
#include <numeric>
#include <memory>
#include <random>
#include <stdexcept>
#include "hidapi.h"
#include "tests/HidTrace.h"
#include "HeadsetController.h"
#include "../Builtins/utilities/yyjson/yyjson.h"

// Independent implementations of the battery-only protocol facts recorded in
// MouseDock DB179 dis_wireless_{hid,halo,controller_hid,...}.txt. No upstream
// HeadsetControl code is linked or copied. Sony H5 stays in its audited extension.
namespace peripheral::headset_controller
{
namespace
{
struct Target { unsigned vid,pid; int mi; unsigned page,usage,revision; const char* adapter; };
const Target targets[] = {
#include "HeadsetControllerTargets.inc"
};
bool Prefix(const Bytes& b,std::initializer_list<unsigned char> p)
{ return b.size()>=p.size()&&std::equal(p.begin(),p.end(),b.begin()); }
Bytes Packet(unsigned size,std::initializer_list<unsigned char> p)
{ if(size<p.size()||size>1024)return {};Bytes b(size,0);std::copy(p.begin(),p.end(),b.begin());return b; }
unsigned Xor(const Bytes& b,size_t first,size_t last)
{ unsigned v=0;for(size_t i=first;i<last;++i)v^=b[i];return v; }
unsigned Sum(const Bytes& b,size_t first,size_t last)
{ return std::accumulate(b.begin()+first,b.begin()+last,0u)&255; }
unsigned U16(const Bytes& b,unsigned at){return b[at]|(unsigned(b[at+1])<<8);}
unsigned Crc16(const Bytes& b)
{ unsigned crc=0;for(auto v:b){crc^=v;for(unsigned i=0;i<8;++i)crc=(crc>>1)^((crc&1)?0xa001:0);}return crc; }
unsigned Crc8(const Bytes& b)
{ unsigned crc=255;for(auto v:b){crc^=v;for(unsigned i=0;i<8;++i)crc=((crc<<1)^((crc&128)?7:0))&255;}return crc; }
uint32_t Crc32(const Bytes& b)
{ uint32_t crc=~0u;for(auto v:b){crc^=v;for(unsigned i=0;i<8;++i)crc=(crc>>1)^((crc&1)?0xedb88320u:0);}return ~crc; }
std::string Lower(std::string s){for(auto& c:s)if(c>='A'&&c<='Z')c+='a'-'A';return s;}
bool Bluetooth(const Candidate& c)
{ auto p=Lower(c.path);return p.find("vid&")!=p.npos||p.find("bth")!=p.npos||p.find("{00001124-0000-1000-8000-00805f9b34fb}")!=p.npos; }
bool Collection(const std::string& path,const char* number)
{ auto p=Lower(path),token=std::string("&col")+number;auto at=p.find(token);return at!=p.npos&&at+token.size()<p.size()&&(p[at+token.size()]=='#'||p[at+token.size()]=='&'||p[at+token.size()]=='\\'); }
bool Io(const Candidate& c,unsigned id,unsigned size)
{return c.input==size&&c.output==size&&c.layout.Has(Report::Input,id,size)&&c.layout.Has(Report::Output,id,size);}
bool Only(const Candidate& c,Report type,unsigned id,unsigned size)
{unsigned count=0;for(const auto& e:c.layout.bytes)if(e.first.first==type){++count;if(e.first.second!=id||e.second!=size)return false;}return c.layout.valid&&count==1;}
bool NoReports(const Candidate& c,Report type)
{for(const auto& e:c.layout.bytes)if(e.first.first==type)return false;return c.layout.valid;}
unsigned Size(const Candidate& c,Report type,unsigned id)
{auto i=c.layout.bytes.find({type,id});return c.layout.valid&&i!=c.layout.bytes.end()?i->second:0;}
bool Geometry(const Candidate& c,const std::string& p)
{
    if(!c.layout.valid||c.input>1024||c.output>1024||c.feature>1024)return false;
    if(p=="sony_ds4"||p=="sony_dualsense")
        return c.page==1&&(c.usage==4||c.usage==5)&&c.input==(Bluetooth(c)?78u:64u)&&
            c.layout.Has(Report::Input,Bluetooth(c)?(p=="sony_ds4"?0x11:0x31):1,c.input);
    if(p=="eightbitdo_input")return c.page==1&&(c.usage==4||c.usage==5)&&c.input>=15&&c.input<=512&&
        Size(c,Report::Input,Bluetooth(c)?1:4)>=15&&Size(c,Report::Input,Bluetooth(c)?1:4)<=64&&Size(c,Report::Input,Bluetooth(c)?1:4)<=c.input;
    if(p=="nintendo_bt")return Bluetooth(c)&&c.page==1&&(c.usage==4||c.usage==5)&&c.input==49&&
        (c.layout.Has(Report::Input,0x21,49)||c.layout.Has(Report::Input,0x30,49));
    if(p=="flydigi_apex5")return c.input==32&&c.output==32&&
        Only(c,Report::Input,4,32)&&Only(c,Report::Output,3,32);
    if(p=="flydigi_vader5")return c.input>=32&&c.input<=65&&c.output>=32&&c.output<=65&&
        Only(c,Report::Input,0,c.input-1)&&Only(c,Report::Output,0,c.output-1);
    if(p=="leadjoy_zhiwan")return c.input==65&&c.output==65&&Only(c,Report::Input,0,64)&&Only(c,Report::Output,0,64);
    if(p=="blackshark2020")return c.page==0xff00&&c.usage==1&&c.input==64&&c.output==64&&c.feature==64&&Only(c,Report::Feature,255,64);
    if(p=="razer_headset")return c.page==0xff00&&c.feature==91&&Only(c,Report::Feature,0,90);
    if(p=="blackshark_v2_hs")return Io(c,2,64); // Its Feature9 report is unrelated to battery I/O.
    if(p=="blackshark_v3_pro")return Io(c,2,64)&&!c.feature;
    if(p=="blackshark_pa"||p=="barracuda_pa")return Io(c,p=="blackshark_pa"?2:1,64);
    if(p=="cloud3")
    {
        auto in=Size(c,Report::Input,0x66),out=Size(c,Report::Output,0x66),feature=Size(c,Report::Feature,0x66);
        return in>=5&&in<=64&&c.input>=in&&c.input<=64&&((out>=62&&out<=64&&c.output==out)||(feature>=62&&feature<=64&&c.feature==feature));
    }
    if(p=="cloud2")return c.input>=20&&c.input<=64&&c.output>=52&&c.output<=64&&
        (c.layout.Has(Report::Input,6,20)||c.layout.Has(Report::Input,6,c.input))&&c.layout.Has(Report::Output,6,c.output);
    if(p=="cloud3s")return ((c.page==0xff13&&c.usage==1)||(c.page==0xffc0&&c.usage==0x202)||
        (c.page==0x1c0&&c.usage==1)||(c.page==0x170f&&c.usage==0x202))&&c.output==64&&
        c.layout.Has(Report::Output,12,64)&&(c.input==0||(c.input>=7&&c.input<=64));
    if(p=="cloud_flight2")return Collection(c.path,"04")&&c.input>=17&&c.input<=1024&&c.output>=2&&c.output<=1024&&
        c.layout.Has(Report::Output,0x52,2)&&c.layout.Has(Report::Output,0x50,2)&&c.layout.Has(Report::Input,0x53,3)&&
        c.layout.Has(Report::Input,0x51,4)&&c.layout.Has(Report::Input,255,17);
    if(p=="epos_h3pro")return c.input>=19&&c.input<=1024&&c.output>=17&&c.output<=1024&&
        Size(c,Report::Input,1)>=19&&Size(c,Report::Input,1)<=c.input&&Size(c,Report::Output,1)>=17&&Size(c,Report::Output,1)<=c.output;
    if(p=="hecate_g5max"||p=="hecate_gx03")return Collection(c.path,"03")&&c.input>=(p=="hecate_gx03"?11u:8u)&&c.input<=64&&c.output>=5&&c.output<=64&&
        c.layout.Has(Report::Input,0xd0,c.input)&&c.layout.Has(Report::Output,0xd0,c.output);
    if(p=="ikf")return c.input==64&&c.layout.Has(Report::Input,3,64)&&
        (c.pid==0x3412?(c.feature==64&&Only(c,Report::Feature,3,64)):(c.output==64&&c.layout.Has(Report::Output,2,64)))&&
        (c.feature!=64||c.layout.Has(Report::Feature,3,64));
    if(p=="moondrop_gm")return c.page>=0xff00&&c.input>=10&&c.output>=4&&
        Size(c,Report::Input,0x4b)>=10&&Size(c,Report::Input,0x4b)<=c.input&&Size(c,Report::Output,0x4b)>=4&&Size(c,Report::Output,0x4b)<=c.output;
    if(p=="alpha2")return c.input==64&&c.output==64&&c.layout.Has(Report::Input,0x51,64)&&c.layout.Has(Report::Output,0x50,64);
    if(p=="jbl910")return c.input>=2&&c.input<=64&&c.layout.Has(Report::Input,8,c.input);
    if(p=="aula_g9")return c.page==0xffab&&c.input>=8&&c.input<=64&&c.layout.Has(Report::Input,0xab,c.input);
    if(p=="vgn_siren")return c.mi==3&&((c.input>=64&&c.output>=64&&c.layout.Has(Report::Input,2,64)&&c.layout.Has(Report::Output,2,64))||
        (c.vid==0x040b&&c.pid==0xf779&&c.input==13&&c.output>=64&&c.output<=65&&c.layout.Has(Report::Input,2,13)&&
        (c.layout.Has(Report::Output,2,64)||c.layout.Has(Report::Output,2,65))));
    if(p=="mchose_v9pro")return c.mi==0&&c.usbProduct=="V9 PRO"&&(c.revision==3||c.revision==0x12||c.revision==0x13)&&Io(c,0x55,64);
    if(p=="vgn_zhou")return c.mi==3&&(c.page>=0xff00||(c.page==12&&c.usage==1))&&c.input>=64&&c.output>=64&&c.layout.Has(Report::Input,0x55,64)&&c.layout.Has(Report::Output,0x55,64); // Never remap 32/32/16.
    if(p=="mchose_frame")return c.page>=0xff00&&c.input>=64&&c.output>=64&&c.layout.Has(Report::Input,0x55,64)&&c.layout.Has(Report::Output,0x55,64);
    if(p=="ikf_legacy")return c.mi==3&&((c.page==0xff02&&c.usage==2)||(c.page==12&&c.usage==1))&&Io(c,0x55,64);
    if(p=="dailing_g3")return (c.input>=64&&c.input<=65&&c.output>=64&&c.output<=65&&
        Size(c,Report::Input,0x55)>=64&&Size(c,Report::Input,0x55)<=c.input&&
        Size(c,Report::Output,0x55)>=64&&Size(c,Report::Output,0x55)<=c.output)||
        (c.revision==0x100&&c.input==65&&c.output==65&&!c.feature&&Only(c,Report::Input,0,64)&&Only(c,Report::Output,0,64));
    if(p=="atk_n9v2"||p=="mchose_v9turbo")
    {
        if(c.usage!=1||c.input!=64||c.output!=64||!c.layout.Has(Report::Input,0x55,64))return false;
        return c.layout.Has(Report::Output,0x55,64)||(c.page==0xff90&&NoReports(c,Report::Output));
    }
    return false;
}

Bytes Request(const Candidate& c,unsigned command=0,unsigned sequence=1)
{
    const auto& p=c.adapter;
    if(p=="alpha2")return Packet(64,{0x50,2});
    if(p=="mchose_frame")return Packet(64,{0x55,0xaa,1,0,1,0,1,0x0b,0});
    if(p=="dailing_g3"&&c.layout.Has(Report::Output,0,64))return Packet(65,{0,0x55,0x65,1});
    if(p=="atk_n9v2"||p=="mchose_v9turbo"||p=="mchose_v9pro"||p=="vgn_zhou"||p=="ikf_legacy"||p=="dailing_g3")return Packet(c.output,{0x55,0x65,1});
    if(p=="vgn_siren")return Packet(c.output,{2,0x0b,1});
    if(p=="flydigi_apex5")return Packet(32,{3,0x5a,0xa5,1,2,0});
    if(p=="flydigi_vader5")return Packet(c.output,{0,0x5a,0xa5,1,2,0});
    if(p=="nintendo_bt")return Packet(49,{1,0,0,1,0x40,0x40,0,1,0x40,0x40,2});
    if(p=="cloud3s"&&(command==6||command==0x48))return Packet(64,{12,2,3,1,0,static_cast<unsigned char>(command)});
    if(p=="cloud_flight2"&&(command==0x52||command==0x50))return Packet(c.output,{static_cast<unsigned char>(command),static_cast<unsigned char>(command==0x52?1:2)});
    if(p=="cloud3")return Packet(c.output>=62?c.output:c.feature,{0x66,0x89});
    if(p=="cloud2")return Packet(c.output,{6,255,0xbb,2,0});
    if(p=="moondrop_gm"&&(command==0x31||command==0x27))return Packet(c.output,{0x4b,0x80,static_cast<unsigned char>(command),0});
    if(p=="hecate_g5max"||p=="hecate_gx03")return Packet(c.output,{0xd0,0x35,0xbb,1,0});
    if(p=="blackshark_v3_pro"||p=="blackshark_v2_hs")
    {if(command!=0x20&&command!=0x21)return {};auto b=Packet(64,{2,0,0x60});b[6]=4;b[9]=command==0x20?0:0x80;b[10]=static_cast<unsigned char>(command);b[62]=static_cast<unsigned char>(Xor(b,0,62));return b;}
    if(p=="blackshark2020")return Packet(64,{255,0x0a,0,0xfd,4,0x12,0xf1,2,5});
    if(p=="razer_headset")
    {auto b=Packet(91,{0,0,0x3f,0,0,0,2,7,0x80});b[89]=static_cast<unsigned char>(Xor(b,3,89));return b;}
    if(p=="blackshark_pa"||p=="barracuda_pa")
    {
        bool black=p=="blackshark_pa";auto b=black?Packet(64,{2,0x80,8,0,0,0x50,0x41,8,0,3,0x21}):Packet(64,{1,0x80,8,0x50,0x41,8,8,3,0x21});
        if(command==1||command==2){b[2]=7;b[black?7:5]=14;unsigned at=black?9:7;b[at]=2;b[at+1]=0xe1;b[at+2]=static_cast<unsigned char>(command==1);}
        else if(command)return {};return b;
    }
    if(p=="epos_h3pro")
    {auto b=Packet(c.output,{1,0xeb,0xfe,1,0,4,0,5,0,4,0});Bytes body{0x12,0,3,0};b[11]=static_cast<unsigned char>(Crc8(body));std::copy(body.begin(),body.end(),b.begin()+13);return b;}
    if(p=="ikf")
    {
        unsigned report=c.pid==0x3412?3:2;
        if(command==0){auto b=Packet(64,{static_cast<unsigned char>(report),7,0x5a,0xaa,0xa5});unsigned crc=Crc16({1,10});b[5]=crc&255;b[6]=crc>>8;b[7]=1;b[8]=10;return b;}
        Bytes payload=command==0xef?Bytes{0xef,1}:command==12?Bytes{0xfa,12}:Bytes{};if(payload.empty())return {};
        auto b=Packet(64,{static_cast<unsigned char>(report),0x55,0x80,0,static_cast<unsigned char>(payload.size()+4),0,255,static_cast<unsigned char>(sequence),static_cast<unsigned char>(payload.size())});
        std::copy(payload.begin(),payload.end(),b.begin()+9);b[9+payload.size()]=0xaa;return b;
    }
    if(p=="leadjoy_zhiwan")
    {const char text[]="{\"c\":14}";auto b=Packet(65,{0,0xa5,12,0xcc});std::copy(text,text+8,b.begin()+4);b[12]=static_cast<unsigned char>(Sum(b,1,12));return b;}
    return {};
}
std::optional<Reading> Value(unsigned value,std::optional<bool> charging={},bool estimated=false)
{if(value>100)return {};Reading r;r.percentage=int(value);r.charging=charging;r.estimated=estimated;return r;}
Reading State(const char* state){Reading r;r.state=state;return r;}
bool UniqueJson(yyjson_val* value,unsigned depth=0)
{
    if(depth>8)return false;
    if(yyjson_is_obj(value))
    {std::set<std::string> keys;size_t i,max;yyjson_val *k,*v;yyjson_obj_foreach(value,i,max,k,v){if(!keys.emplace(yyjson_get_str(k),yyjson_get_len(k)).second||!UniqueJson(v,depth+1))return false;}}
    if(yyjson_is_arr(value))
    {size_t i,max;yyjson_val* v;yyjson_arr_foreach(value,i,max,v){if(!UniqueJson(v,depth+1))return false;}}
    return true;
}
std::optional<Reading> Leadjoy(const Bytes& b)
{
    if(b.size()!=64||!Prefix(b,{0xa5})||b[2]!=0xcc||b[1]<5||b[1]>64||Sum(b,0,b[1]-1)!=b[b[1]-1])return {};
    yyjson_doc* doc=yyjson_read(reinterpret_cast<const char*>(b.data()+3),b[1]-4,0);if(!doc)return {};
    struct Free{yyjson_doc* p;~Free(){yyjson_doc_free(p);}}free{doc};auto root=yyjson_doc_get_root(doc);
    if(!yyjson_is_obj(root)||!UniqueJson(root))return {};
    auto command=yyjson_obj_get(root,"c"),error=yyjson_obj_get(root,"e"),battery=yyjson_obj_get(root,"r");
    if(!yyjson_is_uint(command)||yyjson_get_uint(command)!=14||!yyjson_is_uint(error)||yyjson_get_uint(error)||!yyjson_is_obj(battery))return {};
    for(const char* key:{"mb","sb","cs"}){auto v=yyjson_obj_get(battery,key);if(!yyjson_is_uint(v)||yyjson_get_uint(v)>255)return {};}
    return Value(unsigned(yyjson_get_uint(yyjson_obj_get(battery,"mb"))),yyjson_get_uint(yyjson_obj_get(battery,"cs"))!=0);
}
bool Echo(Bytes reply,Bytes query)
{
    if(reply.empty()||query.empty())return false;
    if(query[0]==0&&reply[0]!=0)query.erase(query.begin());
    auto n=std::min(reply.size(),query.size());
    return std::equal(reply.begin(),reply.begin()+n,query.begin())&&
        std::all_of(reply.begin()+n,reply.end(),[](auto v){return v==0;})&&std::all_of(query.begin()+n,query.end(),[](auto v){return v==0;});
}
std::optional<Reading> DecodeBattery(const Candidate& c,Bytes b,unsigned command=0)
{
    const auto& p=c.adapter;
    auto request=Request(c,command);if(Echo(b,request))return {};
    if((p=="mchose_frame"||p=="vgn_zhou"||p=="vgn_siren")&&b.size()>64&&b.size()==c.input&&std::all_of(b.begin()+64,b.end(),[](auto v){return v==0;}))b.resize(64);
    if(p=="sony_ds4"||p=="sony_dualsense")
    {
        bool bt=Bluetooth(c),ds5=p=="sony_dualsense";unsigned report=bt?(ds5?0x31:0x11):1;
        if(b.size()!=(bt?78:64)||b[0]!=report)return {};
        if(bt){Bytes crc{0xa1};crc.insert(crc.end(),b.begin(),b.end()-4);uint32_t expected=b[74]|(uint32_t(b[75])<<8)|(uint32_t(b[76])<<16)|(uint32_t(b[77])<<24);if(Crc32(crc)!=expected)return {};}
        if(c.pid==0x0ba0&&(b[31]&4))return State("disconnected");
        unsigned status=b[ds5?(bt?54:53):(bt?32:30)],level=status&15;
        if(ds5){unsigned state=status>>4;if(state==2)return Value(100,false,true);if(state>1||level>10)return {};return Value(std::min(100u,level*10+5),state==1,true);}
        bool charging=(status&16)!=0;if(charging&&level==11)return Value(100,false,true);if(level>10)return {};return Value(std::min(100u,level*10+5),charging,true);
    }
    if(p=="eightbitdo_input")
    {unsigned report=Bluetooth(c)?1:4,n=Size(c,Report::Input,report);if(b.size()<15||b.size()>64||b[0]!=report||
        (b.size()!=n&&(b.size()!=c.input||b.size()<n||!std::all_of(b.begin()+n,b.end(),[](auto v){return v==0;})))||(b[14]&127)==0)return {};return Value(b[14]&127,(b[14]&128)!=0&&(b[14]&127)<100);}
    if(p=="nintendo_bt")
    {if(!Bluetooth(c)||b.size()!=49||(b[0]!=0x21&&b[0]!=0x30)||(b[0]==0x21&&(b[13]!=0x82||b[14]!=2)))return {};unsigned band=(b[2]>>5)&7;if(band>4)return {};return Value(band*25,(b[2]&16)!=0,true);}
    if(p=="flydigi_apex5"||p=="flydigi_vader5")
    {
        bool apex=p=="flydigi_apex5";
        if(apex){if(b.size()!=32||b[0]!=4)return {};b.erase(b.begin());}
        else if(b.size()!=c.input-1||b.size()<31||b.size()>64)return {};
        if(!Prefix(b,{0x5a,0xa5,1})||(apex?(b[5]!=128&&b[5]!=129&&b[5]!=133&&b[5]!=134):b[5]!=130))return {};
        unsigned state=b[11]>>4,band=b[11]&15;
        if(apex&&band>5)return {};
        if(state==2){Reading r;r.levelText="5/5";r.charging=false;return r;}
        if(state>1||band>(apex?5u:6u))return {};Reading r;r.levelText=std::to_string(std::min(5u,band+1))+"/5";r.charging=state==1;return r;
    }
    if(p=="leadjoy_zhiwan")return Leadjoy(b);
    if(p=="dailing_g3"&&b.size()==65){if(b.back())return {};b.pop_back();}
    if(p=="atk_n9v2"||p=="mchose_v9turbo"||p=="dailing_g3"||p=="ikf_legacy"||p=="mchose_v9pro"||p=="vgn_zhou")
    {
        if((p=="atk_n9v2"||p=="mchose_v9turbo")&&Prefix(b,{0x55,0x0a,1}))return State("disconnected");
        if(b.size()!=64||!Prefix(b,{0x55,0x65}))return {};
        if(p=="vgn_zhou")return Value(b[2]);
        unsigned state=b[3];if(state==26)return State("sleeping");
        if(p=="mchose_v9pro")
        {if(state==0||state==3){Reading r;r.levelText="External power";return r;}if(state!=2)return {};return Value(b[2],false);}
        if(state<2||state>4)return {};
        // These source profiles explicitly leave charging hardware-unverified.
        // iKF legacy has the documented 2/4 online, 3 charging enum.
        return Value(b[2],(p=="ikf_legacy"||p=="dailing_g3")?std::optional<bool>(state==3):std::nullopt);
    }
    if(p=="mchose_frame")
    {
        if(b.size()!=64||!Prefix(b,{0x55,0xaa,1})||b[3]>1||b[4]!=3||b[5]!=0||b[6]!=1||b[7]!=0x0b)return {};
        if(b[10]!=(b[3]?Xor(b,3,10):0))return {};if(!b[8])return State("sleeping");return Value(b[9]&127);
    }
    if(p=="vgn_siren")
    {if((b.size()!=64&&!(c.vid==0x040b&&c.pid==0xf779&&b.size()==13))||!Prefix(b,{2,0x0b}))return {};if(!b[2])return State("disconnected");if(b[2]!=1||b[4]>9)return {};return Value((b[4]+1)*10,{},true);}
    if(p=="alpha2")
    {if(b.size()<7||b.size()>64||!Prefix(b,{0x51,2}))return {};return Value(b[2],(b[6]&128)!=0);}
    if(p=="jbl910")return b.size()==c.input&&Prefix(b,{8})?Value(b[1]):std::nullopt;
    if(p=="aula_g9")
    {if(b.size()!=c.input||!Prefix(b,{0xab,0x47,0x4d,0x86})||(b[4]!=3&&b[4]!=4)||b.size()<5u+b[4])return {};unsigned at=b[4]==3?6:5;if(b[4]==3&&b[5]!=1)return {};if(!b[at+1])return State("disconnected");if(b[at+1]!=1)return {};return Value(b[at]);}
    if(p=="blackshark2020")
    {if(b.size()!=64||!Prefix(b,{255,0x0f,5,0xfe,0x12,4,0x1f,8,5})||b[14]>100)return {};if(!b[12]&&!b[13])return State("disconnected");if(b[11]==9){Reading r;r.charging=true;return r;}return Value(b[14],{},true);}
    if(p=="razer_headset")
    {if(b.size()!=91||!Prefix(b,{0,2,0x3f,0,0,0,2,7,0x80})||b[90]||Xor(b,3,89)!=b[89])return {};return Value((unsigned(b[10])*100+127)/255);}
    if(p=="blackshark_v3_pro")
    {
        unsigned len=command==0x20?2:1;
        if(b.size()!=64||!Prefix(b,{2,2,0x60})||b[6]!=4+len||b[7]||b[8]||b[9]!=(command==0x20?0:0x80)||b[10]!=command||b[11]!=1||b[12]!=len||b[62]!=Xor(b,0,62))return {};
        if(command==0x20){if(b[13]>1||b[14])return {};return b[13]?Value(1):std::optional<Reading>(State("disconnected"));}return Value(b[13]);
    }
    if(p=="blackshark_v2_hs")
    {
        // Captured 0565 replies: one-byte link/battery payload, flag80 at byte8,
        // XOR excluding report ID. A successful 0020 payload00 means offline;
        // silence, error statuses and malformed frames are never disconnects.
        if((command!=0x20&&command!=0x21)||b.size()!=64||
            !Prefix(b,{2,2,0x60,0,0,0,5,0,0x80})||b[9]!=(command==0x20?0:0x80)||
            b[10]!=command||b[11]!=1||b[12]!=1||b[62]!=Xor(b,1,62)||b[63]||
            std::any_of(b.begin()+14,b.begin()+62,[](auto v){return v!=0;}))return {};
        if(command==0x20){if(b[13]>1)return {};return b[13]?Value(1):std::optional<Reading>(State("disconnected"));}
        return Value(b[13]);
    }
    if(p=="blackshark_pa"||p=="barracuda_pa")
    {bool black=p=="blackshark_pa";unsigned at=black?12:13;if(b.size()!=64||b[0]!=(black?2:1)||(!black&&(!Prefix(b,{1,0x80})||b[3]!=0x50||b[4]!=0x49||b[5]!=8))||b[at]!=0x21||b[at+1]<1||b[at+1]>(black?1:2)||b[at+2]<1||b[at+2]>32||at+3u+b[at+2]>b.size())return {};return Value(b[at+3]);}
    if(p=="cloud3")return b.size()>=5&&b.size()<=64&&b[0]==0x66&&(b[1]==0x89||b[1]==0x0d)&&(b[2]||b[3])?Value(b[4]):std::nullopt;
    if(p=="cloud2")return b.size()==20&&Prefix(b,{6,255,0xbb,2})&&std::any_of(b.begin()+4,b.end(),[](auto v){return v!=0;})?Value(b[7]):std::nullopt;
    if(p=="cloud3s")
    {if(b.size()<7||b.size()>64||request.size()!=64||!std::equal(b.begin(),b.begin()+6,request.begin())||(command==0x48&&b[6]>2))return {};return Value(b[6]);}
    if(p=="hecate_g5max"||p=="hecate_gx03")
    {
        bool tws=p=="hecate_gx03";
        if(b.size()!=c.input||!Prefix(b,{0xd0,0x35,0xbb})||b[4]||b[3]<(tws?7:4)||unsigned(b[3])+4>b.size())return {};
        if(tws)
        {if(!(b[5]&3))return State("disconnected");Reading r;for(unsigned slot=0;slot<2;++slot)if(b[5]&(1u<<slot)){auto v=Value(b[6+slot]&127);if(!v)return {};v->role=slot?"right":"left";r.components.push_back(*v);}return r;}
        if(!b[5])return State("disconnected");if(b[5]!=1||b[7]>2)return {};return Value(b[6]);
    }
    if(p=="moondrop_gm")
    {if((b.size()!=c.input&&b.size()!=Size(c,Report::Input,0x4b))||b.size()<10||b[0]!=0x4b||b[2]!=command)return {};if(command==0x31)return b[4]?Value(1):std::optional<Reading>(State("disconnected"));if(!b[8])return {};return Value(b[9]);}
    if(p=="epos_h3pro")
    {unsigned size=Size(c,Report::Input,1);if((b.size()!=size&&b.size()!=c.input)||b.size()<19||!Prefix(b,{1,0xeb,0xfe,1,0})||b[6]||U16(b,9)!=6||b[13]!=0x12||b[14]||b[15]!=3||b[16]||Crc8(Bytes(b.begin()+13,b.begin()+19))!=b[11])return {};return Value(b[17]|b[18]);}
    return {};
}

std::optional<unsigned> IkfStatus(const Bytes& b)
{
    if(b.size()!=64||b[0]!=3)return {};
    Bytes payload;unsigned crc=0;
    if(b[1]==0xd6&&b[2]==0xbe)
    {if(U16(b,4)!=1||b[6]!=10)return {};payload={b[9]};crc=U16(b,7);}
    else if(b[2]==0xd6&&b[3]==0xbe)
    {if(U16(b,5)!=2||b[1]!=9||b[9]!=10)return {};payload={b[9],b[10]};crc=U16(b,7);}
    else return {};
    if(Crc16(payload)!=crc)return {};return unsigned(payload.back());
}
std::optional<Bytes> IkfApp(const Bytes& b,unsigned sequence,unsigned command)
{
    if(b.size()!=64||!Prefix(b,{3,0x55,0x80,0}))return {};unsigned n=U16(b,4);
    if(n<5||n>58||std::any_of(b.begin()+6+n,b.end(),[](auto v){return v!=0;}))return {};
    Bytes app(b.begin()+6,b.begin()+6+n);
    if(app[0]!=255||app[1]!=sequence||app.back()!=0xaa||app[2]!=app.size()-4)return {};
    Bytes body;
    if(command==12&&app[3]==0xfa&&app.size()>5&&app[4]==12)body.assign(app.begin()+5,app.end()-1);
    else if(app[3]==command)body.assign(app.begin()+4,app.end()-1);
    else return {};
    if((command==0xef&&body.size()==4)||(command==12&&(body.size()==3||body.size()==4)))return body;
    return {};
}
struct Device
{
    hid_device* h=nullptr;
    explicit Device(const std::string& path):h(hid_open_path(path.c_str())){}
    ~Device(){if(h)hid_close(h);}
    Device(const Device&)=delete;Device& operator=(const Device&)=delete;
};
bool SendOnce(const std::function<bool()>& initialize,const std::function<bool()>& send)
{return !initialize||!initialize()||send();}
using Clock=std::chrono::steady_clock;
struct Session
{
    hid_device* h;
    hid_device* input;
    const Candidate& c;
    const std::function<void()>& progress;
    Clock::time_point end=Clock::now()+std::chrono::milliseconds(7200);
    bool Alive()const{return Clock::now()<end;}
    void Progress(){if(progress)progress();}
    bool Drain()
    {Progress();unsigned char b[1024];for(unsigned i=0;i<32&&Alive();++i){int n=hid_read_timeout(input,b,sizeof(b),1);if(n<0)return false;if(!n)return true;}return false;}
    bool Send(const Bytes& b,bool feature=false,bool control=false)
    {if(b.empty()||!Alive())return false;Progress();int n=feature?hid_send_feature_report(h,b.data(),b.size()):control?hid_send_output_report(h,b.data(),b.size()):hid_write(h,b.data(),b.size());return n==int(b.size())&&Alive();}
    Bytes Next(unsigned wait=40)
    {if(!Alive())return {};Progress();Bytes b(1024);int n=hid_read_timeout(input,b.data(),b.size(),int(std::min(wait,40u)));if(n<=0)return {};b.resize(n);return b;}
    Bytes Feature(unsigned id,unsigned size)
    {if(!Alive()||!size||size>1024)return {};Progress();Bytes b(size,0);b[0]=static_cast<unsigned char>(id);int n=hid_get_feature_report(h,b.data(),b.size());if(n<=0||n>int(size)||!Alive())return {};b.resize(n);return b;}
    template<class F> auto Wait(F decode,unsigned ms=1000,bool feature=false,unsigned featureId=0,unsigned featureSize=0)->decltype(decode(Bytes{}))
    {
        auto until=std::min(end,Clock::now()+std::chrono::milliseconds(ms));
        for(unsigned i=0;i<256&&Clock::now()<until;++i)
        {
            auto b=feature?Feature(featureId,featureSize):Next();
            if(!b.empty())if(auto result=decode(b))return result;
            if(feature)Sleep(40);
        }
        return {};
    }
    std::optional<Reading> Exchange(unsigned command=0)
    {
        bool feature=c.adapter=="cloud3"&&c.output<62;
        if(!Drain()||!Send(Request(c,command),feature,c.adapter=="blackshark_v3_pro"||c.adapter=="blackshark_v2_hs"))return {};
        auto result=Wait([&](const Bytes& b){return DecodeBattery(c,b,command);});
        if(c.adapter=="moondrop_gm"&&Alive())Sleep(50);return result;
    }
};

std::wstring Wide(const std::string& s)
{int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),int(s.size()),nullptr,0);std::wstring v(n,0);if(n)MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),int(s.size()),v.data(),n);return v;}
std::string Utf8(const wchar_t* s)
{if(!s)return {};int n=WideCharToMultiByte(CP_UTF8,0,s,-1,nullptr,0,nullptr,nullptr);if(n<=1)return {};std::string v(n,0);WideCharToMultiByte(CP_UTF8,0,s,-1,v.data(),n,nullptr,nullptr);v.pop_back();return v;}
std::string Container(const std::string& path)
{
    HDEVINFO list=SetupDiCreateDeviceInfoList(nullptr,nullptr);if(list==INVALID_HANDLE_VALUE)return {};
    struct Free{HDEVINFO h;~Free(){SetupDiDestroyDeviceInfoList(h);}}free{list};
    SP_DEVICE_INTERFACE_DATA iface{};iface.cbSize=sizeof(iface);auto wide=Wide(path);
    if(!SetupDiOpenDeviceInterfaceW(list,wide.c_str(),0,&iface))return {};
    DWORD n=0;SetupDiGetDeviceInterfaceDetailW(list,&iface,nullptr,0,&n,nullptr);if(n<sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)||n>32768)return {};
    Bytes b(n);auto detail=reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(b.data());detail->cbSize=sizeof(*detail);
    SP_DEVINFO_DATA info{};info.cbSize=sizeof(info);
    if(!SetupDiGetDeviceInterfaceDetailW(list,&iface,detail,n,nullptr,&info))return {};
    GUID id{};DEVPROPTYPE type=0;DWORD size=0;wchar_t text[40]{};
    if(!SetupDiGetDevicePropertyW(list,&info,&DEVPKEY_Device_ContainerId,&type,reinterpret_cast<PBYTE>(&id),sizeof(id),&size,0)||
        type!=DEVPROP_TYPE_GUID||IsEqualGUID(id,GUID_NULL)||!StringFromGUID2(id,text,40))return {};
    return Lower(Utf8(text));
}
bool Caps(Candidate& c,hid_device* h)
{
    auto path=Wide(c.path);HANDLE native=CreateFileW(path.c_str(),0,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,0,nullptr);
    if(native==INVALID_HANDLE_VALUE)return false;
    PHIDP_PREPARSED_DATA data=nullptr;HIDP_CAPS caps{};
    bool ok=HidD_GetPreparsedData(native,&data)&&HidP_GetCaps(data,&caps)==HIDP_STATUS_SUCCESS;
    if(data)HidD_FreePreparsedData(data);CloseHandle(native);if(!ok)return false;
    if(c.page!=caps.UsagePage||c.usage!=caps.Usage)return false;
    c.input=caps.InputReportByteLength;c.output=caps.OutputReportByteLength;c.feature=caps.FeatureReportByteLength;
    Bytes descriptor(4096);int n=hid_get_report_descriptor(h,descriptor.data(),descriptor.size());if(n<=0||n>4096)return false;
    descriptor.resize(n);c.layout=ParseDescriptor(descriptor);return c.layout.valid;
}
struct InputCollection {std::unique_ptr<Device> device;Candidate candidate;};
std::optional<InputCollection> CloudInput(const Candidate& c,const std::function<void()>& progress)
{
    if(c.physical.empty())return {};
    if(progress)progress();auto list=hid_enumerate(static_cast<unsigned short>(c.vid),static_cast<unsigned short>(c.pid));
    struct Free{hid_device_info* p;~Free(){hid_free_enumeration(p);}}free{list};
    std::optional<InputCollection> result;unsigned count=0;
    for(auto item=list;item;item=item->next)
    {
        if(++count>64)return {};
        if(item->vendor_id!=c.vid||item->product_id!=c.pid||item->release_number!=c.revision||item->interface_number!=3||!item->path)continue;
        bool collection=(item->usage_page==0xff13&&item->usage==1)||(item->usage_page==0xffc0&&item->usage==0x202)||
            (item->usage_page==0x1c0&&item->usage==1)||(item->usage_page==0x170f&&item->usage==0x202);
        if(!collection)continue;
        if(progress)progress();if(Container(item->path)!=c.physical)continue;
        auto device=std::make_unique<Device>(item->path);if(!device->h)continue;
        Candidate peer=c;peer.path=item->path;peer.page=item->usage_page;peer.usage=item->usage;
        if(!Caps(peer,device->h)||peer.input<7||peer.input>64||Size(peer,Report::Input,12)<7||Size(peer,Report::Input,12)>peer.input||Container(peer.path)!=c.physical)continue;
        if(result)return {};result=InputCollection{std::move(device),std::move(peer)};
    }
    return result;
}

enum class FlightResult { Continue, Complete, Retry, Offline, Invalid };
FlightResult FlightFrame(const Candidate& c,const Bytes& b,unsigned command,bool& ack,std::optional<Reading>& response)
{
    if(b.empty())return FlightResult::Continue;
    unsigned declared=Size(c,Report::Input,b[0]);auto packet=Request(c,command);
    if(packet.empty()||!declared||b.size()<declared||b.size()>c.input)return FlightResult::Continue;
    if(Prefix(b,{0xfb,10})&&b.size()>=3&&b[2])return FlightResult::Offline;
    if(Prefix(b,{255,1})&&b.size()>=17&&b[14]==packet[0]&&b[15]==packet[1])
    {if(b[16]==1)return FlightResult::Retry;if(b[16])return FlightResult::Invalid;ack=true;}
    else if(b.size()>=3&&b[0]==command+1&&b[1]==packet[1])
    {
        if(command==0x52)
        {
            bool exception=(c.revision==0x4110||c.revision==0x4112)&&c.page==0xff90&&c.usage==0xff00&&c.input==64&&c.output==64;
            if(b[2]&&!(exception&&b[2]==1))return FlightResult::Offline;response=Value(1);
        }
        else if(b.size()>=4)response=Value(b[2],b[3]<=1?std::optional<bool>(b[3]==1):std::nullopt);
    }
    return ack&&response?FlightResult::Complete:FlightResult::Continue;
}
std::optional<Reading> FlightExchange(Session& io,unsigned command)
{
    const auto& c=io.c;auto packet=Request(c,command);
    for(unsigned attempt=0;attempt<4&&io.Alive();++attempt)
    {
        if(!io.Drain()||!io.Send(packet))return {};
        bool ack=false,retry=false;std::optional<Reading> response;
        auto started=Clock::now(),until=std::min(io.end,started+std::chrono::milliseconds(2000));
        while(Clock::now()<until)
        {
            if(!ack&&Clock::now()-started>=std::chrono::milliseconds(500))return {};
            auto result=FlightFrame(c,io.Next(),command,ack,response);
            if(result==FlightResult::Retry){retry=true;break;}
            if(result==FlightResult::Invalid)return {};
            if(result==FlightResult::Offline)return State("disconnected");
            if(result==FlightResult::Complete)return response;
        }
        if(!retry)return {};
    }
    return {};
}

std::optional<Reading> IkfRead(Session& io)
{
    const auto& c=io.c;unsigned sequence=std::random_device{}()&255;
    auto exchange=[&](unsigned command)->std::optional<Bytes>{
        auto packet=Request(c,command,sequence);
        if(!io.Drain()||!io.Send(packet,c.pid==0x3412))return {};
        auto until=std::min(io.end,Clock::now()+std::chrono::milliseconds(1200));
        while(Clock::now()<until)
        {
            auto parse=[&](const Bytes& b)->std::optional<Bytes>{
                if(command==0){auto state=IkfStatus(b);return state?std::optional<Bytes>(Bytes{static_cast<unsigned char>(*state)}):std::nullopt;}
                return IkfApp(b,sequence,command);
            };
            auto b=io.Next();if(auto v=parse(b))return v;
            if(c.feature==64){auto feature=io.Feature(3,64);if(auto v=parse(feature))return v;}
        }
        return {};
    };
    auto connected=[&](){auto v=exchange(0);return v&&v->size()==1&&(*v)[0]==1;};
    auto identity=[&](){sequence=(sequence+1)&255;auto v=exchange(0xef);unsigned mid=c.pid==0x3411?0x3412:c.pid==0x3412?0x3411:c.pid;return v&&v->size()==4&&U16(*v,0)==0x381f&&U16(*v,2)==mid;};
    if(!connected()||!identity())return {};
    std::optional<Reading> result;
    for(unsigned round=0;round<2;++round)
    {sequence=(sequence+1)&255;auto b=exchange(12);if(!b||(b->size()!=3&&b->size()!=4)||!(result=Value((*b)[0])))return {};}
    if(!identity()||!connected())return {};return result;
}
}

bool KnownIdentity(unsigned vid,unsigned pid)
{for(const auto& t:targets)if(t.vid==vid&&t.pid==pid)return true;return false;}
bool Admit(Candidate& c)
{
    const Target* found=nullptr;
    for(const auto& t:targets)
    {
        if(c.vid!=t.vid||c.pid!=t.pid||(t.mi>=0&&c.mi!=t.mi)||(t.page&&c.page!=t.page)||(t.usage&&c.usage!=t.usage)||
            (t.revision&&c.revision!=t.revision)||!Geometry(c,t.adapter))continue;
        if(found&&std::strcmp(found->adapter,t.adapter))return false;found=&t;
    }
    if(!found)return false;
    c.protocol=Protocol::HeadsetController;c.adapter=found->adapter;c.identityOnly=false;
    c.kind=c.adapter=="hecate_gx03"?"earbuds":(c.adapter=="sony_ds4"||c.adapter=="sony_dualsense"||c.adapter=="eightbitdo_input"||c.adapter=="nintendo_bt"||c.adapter=="flydigi_apex5"||c.adapter=="flydigi_vader5"||c.adapter=="leadjoy_zhiwan")?"gamepad":"headset";
    if(!c.usbProduct.empty())c.name=c.usbProduct;
    else if(c.name.empty())c.name=c.kind=="gamepad"?"Wireless controller":"Wireless headset";
    return true;
}

std::optional<Reading> Read(const Candidate& c,const std::function<void()>& progress,const std::function<bool()>& initialize)
{
    auto deadline=Clock::now()+std::chrono::milliseconds(7000);
    Candidate admitted=c;if(!Admit(admitted)||admitted.adapter!=c.adapter||c.path.empty())return {};
    if(progress)progress();Device device(c.path);if(!device.h)return {};
    // Re-read the already-open handle's metadata/descriptor before any write.
    auto info=hid_get_device_info(device.h);
    if(!info||info->vendor_id!=c.vid||info->product_id!=c.pid||info->usage_page!=c.page||info->usage!=c.usage||
        info->release_number!=c.revision||info->interface_number!=c.mi||
        (c.adapter=="mchose_v9pro"&&Utf8(info->product_string)!="V9 PRO"))return {};
    Candidate live=c;if(!Caps(live,device.h)||!Admit(live)||live.adapter!=c.adapter||live.layout.bytes!=c.layout.bytes||
        live.input!=c.input||live.output!=c.output||live.feature!=c.feature||c.physical.empty()||Container(c.path)!=c.physical)return {};
    std::optional<InputCollection> split;
    if(c.adapter=="cloud3s")
    {split=CloudInput(c,progress);if(!split)return {};}
    Session io{device.h,split?split->device->h:device.h,c,progress,deadline};if(!io.Alive())return {};const auto& p=c.adapter;
    if(p=="ikf")return IkfRead(io);
    if(p=="sony_ds4"||p=="sony_dualsense"||p=="eightbitdo_input"||p=="jbl910"||p=="aula_g9"||p=="nintendo_bt")
    {
        auto decode=[&](const Bytes& b){return DecodeBattery(c,b);};
        auto result=io.Wait(decode,p=="nintendo_bt"?150:1400);
        if(p=="nintendo_bt"&&!result&&c.output>=49&&c.layout.Has(Report::Output,1,49))
        {
            // Read-only device-info subcommand, once per parent-owned journal.
            // Denial/reconnect never repeats initialization; continue listening.
            if(!SendOnce(initialize,[&](){return io.Send(Request(c));}))return {};
            result=io.Wait(decode,2000);
        }
        if(!result||result->state!="online")return result;
        return io.Wait(decode,1400);
    }
    std::optional<Reading> result;
    if(p=="blackshark_pa"||p=="barracuda_pa")
    {
        // E1 selects the temporary remote read route, not an audio setting.
        // Always restore it, including query failure. It is not a wake/init.
        if(!io.Drain())return {};
        struct Restore
        {
            Session& io;bool active=true;
            bool Close(){if(!active)return true;active=false;io.Progress();auto b=Request(io.c,2);return hid_write(io.h,b.data(),b.size())==int(b.size());}
            ~Restore(){if(active)Close();}
        }restore{io};
        if(!io.Send(Request(c,1)))return {};Sleep(p=="blackshark_pa"?35:50);
        for(unsigned round=0;round<2;++round)
        {if(p=="blackshark_pa"){if(!io.Send(Request(c,1)))return {};Sleep(35);}if(!io.Drain()||!io.Send(Request(c)))return {};result=io.Wait([&](const Bytes& b){return DecodeBattery(c,b);},900);if(!result)return {};}
        if(!restore.Close())return {};return result;
    }
    for(unsigned round=0;round<2&&io.Alive();++round)
    {
        if(p=="blackshark2020"||p=="razer_headset")
        {if(!io.Send(Request(c),true))return {};Sleep(p=="blackshark2020"?100:80);result=io.Wait([&](const Bytes& b){return DecodeBattery(c,b);},500,true,p=="blackshark2020"?255:0,p=="blackshark2020"?64:91);}
        else if(p=="cloud_flight2")
        {auto link=FlightExchange(io,0x52);if(!link||link->state!="online")return link;result=FlightExchange(io,0x50);}
        else if(p=="cloud3s")
        {
            if(Container(split->candidate.path)!=c.physical||Container(c.path)!=c.physical)return {};
            result=io.Exchange(6);if(!result)return {};auto charge=io.Exchange(0x48);if(!charge||!charge->percentage)return {};result->charging=*charge->percentage==1||*charge->percentage==2;
        }
        else if(p=="moondrop_gm"||p=="blackshark_v2_hs"||(p=="blackshark_v3_pro"&&c.pid==0x101b))
        {
            unsigned link=p=="moondrop_gm"?0x31:0x20,battery=p=="moondrop_gm"?0x27:0x21;
            auto online=io.Exchange(link);if(!online||online->state!="online")return online;
            result=io.Exchange(battery);if(!result)return {};online=io.Exchange(link);if(!online||online->state!="online")return online;
        }
        else result=io.Exchange(p=="blackshark_v2_hs"||p=="blackshark_v3_pro"?0x21:0);
        if(!result||result->state!="online")return result;
    }
    return io.Alive()?result:std::nullopt;
}

void OfflineSelfTests()
{
    auto check=[](bool ok,const char* why){if(!ok)throw std::runtime_error(std::string("Headset/controller: ")+why);};
    auto fixture=[](const Target& t)
    {
        Candidate c;c.vid=t.vid;c.pid=t.pid;c.mi=t.mi<0?0:t.mi;c.page=t.page?t.page:0xff00;c.usage=t.usage?t.usage:1;c.revision=t.revision;
        c.path="fixture&col03#device";c.layout.valid=true;c.adapter=t.adapter;
        auto report=[&](Report type,unsigned id,unsigned n){c.layout.bytes[{type,id}]=n;};
        auto io=[&](unsigned id,unsigned n){c.input=c.output=n;report(Report::Input,id,n);report(Report::Output,id,n);};
        const auto& p=c.adapter;
        if(p=="sony_ds4"||p=="sony_dualsense"){c.page=1;c.usage=5;c.input=64;report(Report::Input,1,64);}
        else if(p=="eightbitdo_input"){c.page=1;c.usage=5;c.input=64;report(Report::Input,4,64);}
        else if(p=="nintendo_bt"){c.page=1;c.usage=5;c.path="bth#vid&057e";c.input=c.output=49;report(Report::Input,0x30,49);report(Report::Input,0x21,49);report(Report::Output,1,49);}
        else if(p=="flydigi_apex5"){c.input=c.output=32;report(Report::Input,4,32);report(Report::Output,3,32);}
        else if(p=="flydigi_vader5"||p=="leadjoy_zhiwan"){c.input=c.output=65;report(Report::Input,0,64);report(Report::Output,0,64);}
        else if(p=="blackshark2020"){c.input=c.output=c.feature=64;report(Report::Feature,255,64);}
        else if(p=="razer_headset"){c.feature=91;report(Report::Feature,0,90);}
        else if(p=="blackshark_pa"||p=="barracuda_pa")io(p=="blackshark_pa"?2:1,64);
        else if(p=="blackshark_v3_pro"||p=="blackshark_v2_hs")io(2,64);
        else if(p=="cloud3")io(0x66,64);
        else if(p=="cloud2"){c.input=20;c.output=52;report(Report::Input,6,20);report(Report::Output,6,52);}
        else if(p=="cloud3s"){c.page=0xff13;c.input=7;c.output=64;report(Report::Input,12,7);report(Report::Output,12,64);}
        else if(p=="cloud_flight2")
        {c.page=0xff90;c.usage=0xff00;c.path="fixture&col04#device";c.input=c.output=64;report(Report::Output,0x52,2);report(Report::Output,0x50,2);report(Report::Input,0x53,3);report(Report::Input,0x51,4);report(Report::Input,255,17);}
        else if(p=="hecate_g5max"||p=="hecate_gx03")io(0xd0,64);
        else if(p=="epos_h3pro"){c.input=19;c.output=17;report(Report::Input,1,19);report(Report::Output,1,17);}
        else if(p=="ikf")
        {c.input=64;report(Report::Input,3,64);if(c.pid==0x3412){c.feature=64;report(Report::Feature,3,64);}else{c.output=64;report(Report::Output,2,64);}}
        else if(p=="moondrop_gm")io(0x4b,64);
        else if(p=="alpha2"){c.input=c.output=64;report(Report::Input,0x51,64);report(Report::Output,0x50,64);}
        else if(p=="jbl910"){c.input=64;report(Report::Input,8,64);}
        else if(p=="aula_g9"){c.input=64;report(Report::Input,0xab,64);}
        else if(p=="vgn_siren"){c.mi=3;io(2,64);}
        else if(p=="vgn_zhou"){c.mi=3;io(0x55,64);}
        else if(p=="mchose_v9pro"){c.mi=0;c.usbProduct="V9 PRO";c.revision=3;io(0x55,64);}
        else if(p=="ikf_legacy"){c.page=0xff02;c.usage=2;io(0x55,64);}
        else if(p=="dailing_g3")io(0x55,65);
        else io(0x55,64);
        return c;
    };
    auto candidate=[&](const char* p)
    {for(const auto& t:targets)if(std::strcmp(p,t.adapter)==0)return fixture(t);throw std::runtime_error("Missing headset fixture target");};
    std::set<std::string> families;
    for(const auto& t:targets)
    {
        auto c=fixture(t);check(KnownIdentity(c.vid,c.pid),"known ID missing");check(Admit(c),t.adapter);
        check(c.protocol==Protocol::HeadsetController&&c.adapter==t.adapter&&!c.identityOnly,"admission contract");families.insert(c.adapter);
        auto bad=c;bad.pid=0xffff;check(!Admit(bad),"unknown PID admitted");
        bad=c;bad.layout.valid=false;check(!Admit(bad),"invalid descriptor admitted");
        if(t.mi>=0){bad=c;bad.mi=t.mi+1;check(!Admit(bad),"foreign interface admitted");}
        if(t.page){bad=c;bad.page=0x42;check(!Admit(bad),"foreign usage page admitted");}
        if(t.usage){bad=c;bad.usage=0x777;check(!Admit(bad),"foreign usage admitted");}
        check(!DecodeBattery(c,{}),"empty input accepted");check(!DecodeBattery(c,Bytes(1025,0)),"oversized input accepted");
        auto request=Request(c,c.adapter=="cloud3s"?6:c.adapter=="blackshark_v3_pro"||c.adapter=="blackshark_v2_hs"?0x21:0);
        if(!request.empty())check(!DecodeBattery(c,request,c.adapter=="cloud3s"?6:0),"query echo accepted");
    }
    check(families.size()==33,"family fixture coverage changed");
    for(auto id:{0x0ebfu,0x0e53u})check(!KnownIdentity(0x054c,id),"Sony extension/existing serial duplicated");
    for(auto id:{0x057au})check(!KnownIdentity(0x1532,id),"existing passive BlackShark duplicated");
    check(!KnownIdentity(0x0b05,0x1b84)&&!KnownIdentity(0x0b05,0x1afa),"existing ROG duplicated");
    auto checkValue=[&](const Candidate& c,const Bytes& b,int expected,unsigned command=0)
    {auto r=DecodeBattery(c,b,command);check(r&&r->percentage==expected,"battery fixture");};
    auto c=candidate("atk_n9v2");auto b=Packet(64,{0x55,0x65,73,2});checkValue(c,b,73);
    auto disconnected=DecodeBattery(c,{0x55,0x0a,1});check(disconnected&&disconnected->state=="disconnected"&&!disconnected->percentage,"short explicit disconnect");
    check(!DecodeBattery(c,Packet(64,{0x55,0x65,73,5})),"unknown status accepted");
    auto sleeping=DecodeBattery(c,Packet(64,{0x55,0x65,0,26}));check(sleeping&&sleeping->state=="sleeping"&&!sleeping->percentage,"sleep changed into zero");
    c=candidate("mchose_v9turbo");c.page=0xff90;c.layout.bytes.erase({Report::Output,0x55});check(Admit(c),"documented Turbo input-only channel rejected");
    c.page=0xff82;check(!Admit(c),"input-only exception widened");
    c=candidate("vgn_zhou");c.input=c.output=32;c.feature=16;c.layout.bytes.clear();c.layout.bytes[{Report::Input,0x55}]=32;c.layout.bytes[{Report::Output,0x55}]=32;check(!Admit(c),"Zhou unsupported 32-byte remap admitted");
    c=candidate("mchose_v9pro");checkValue(c,Packet(64,{0x55,0x65,50,2}),50);
    auto external=DecodeBattery(c,Packet(64,{0x55,0x65,50,0}));check(external&&!external->percentage&&!external->charging&&external->levelText=="External power","V9 Pro frozen level exposed");
    c.usbProduct="Other headset";check(!Admit(c),"V9 Pro name proof ignored");
    c=candidate("dailing_g3");b=Packet(65,{0x55,0x65,60,4});checkValue(c,b,60);b.back()=1;check(!DecodeBattery(c,b),"nonzero Dailing padding accepted");
    b=Packet(65,{0x55,0x65,60,3});check(DecodeBattery(c,b)->charging==true,"DAILING charging status 3");
    c.revision=0x100;c.layout.bytes.clear();c.layout.bytes[{Report::Input,0}]=64;c.layout.bytes[{Report::Output,0}]=64;
    check(Admit(c)&&Prefix(Request(c),{0,0x55,0x65,1}),"DAILING native65 unnumbered route");checkValue(c,Packet(64,{0x55,0x65,60,2}),60);
    c.revision=0x101;check(!Admit(c),"DAILING unnumbered exception widened");
    c=candidate("ikf_legacy");auto charging=DecodeBattery(c,Packet(64,{0x55,0x65,50,3}));check(charging&&charging->charging==true,"legacy iKF charging enum");
    c=candidate("vgn_siren");checkValue(c,Packet(64,{2,0x0b,1,0,4}),50);check(!DecodeBattery(c,Packet(64,{2,0x0b,1,0,255})),"unknown Siren band accepted");
    c.input=13;c.layout.bytes[{Report::Input,2}]=13;check(Admit(c),"F779 short status");checkValue(c,Packet(13,{2,0x0b,1,0,4}),50);
    c.vid=0x391d;c.pid=3;check(!Admit(c),"short status exception widened to Siren V2");
    c=candidate("mchose_frame");b=Packet(64,{0x55,0xaa,1,1,3,0,1,0x0b,1,80});b[10]=static_cast<unsigned char>(Xor(b,3,10));checkValue(c,b,80);b[10]^=1;check(!DecodeBattery(c,b),"frame checksum ignored");
    c=candidate("alpha2");b=Packet(64,{0x51,2,85});b[6]=128;charging=DecodeBattery(c,b);check(charging&&charging->percentage==85&&charging->charging==true,"Alpha charge flag");
    c=candidate("jbl910");checkValue(c,Packet(64,{8,45}),45);check(Request(c).empty(),"JBL must be passive");
    c=candidate("aula_g9");b=Packet(64,{0xab,0x47,0x4d,0x86,3,1,77,1});checkValue(c,b,77);b[7]=0;auto offline=DecodeBattery(c,b);check(offline&&offline->state=="disconnected","G9 offline ignored");check(Request(c).empty(),"G9 must be passive");
    c=candidate("cloud2");checkValue(c,Packet(20,{6,255,0xbb,2,0,0,0,65}),65);
    c=candidate("cloud3");checkValue(c,Packet(64,{0x66,0x0d,1,0,54}),54);check(!DecodeBattery(c,Packet(64,{0x66,0x0d,0,0,54})),"unavailable Cloud III accepted");
    c=candidate("cloud3s");b=Packet(7,{12,2,3,1,0,6,87});checkValue(c,b,87,6);check(!DecodeBattery(c,b,0x48),"Cloud III S command mismatch");
    c=candidate("cloud_flight2");check(Geometry(c,c.adapter),"Flight2 descriptor");c.path="fixture&col03#device";check(!Admit(c),"Flight2 wrong collection");
    c=candidate("cloud_flight2");bool ack=false;std::optional<Reading> flight;
    b=Packet(17,{255,1});b[14]=0x50;b[15]=2;
    check(FlightFrame(c,b,0x50,ack,flight)==FlightResult::Continue&&ack&&!flight,"Flight2 ACK accepted without battery");
    check(FlightFrame(c,Packet(4,{0x51,2,88,1}),0x50,ack,flight)==FlightResult::Complete&&flight->percentage==88&&flight->charging==true,"Flight2 correlated battery");
    ack=false;flight.reset();b[15]=1;check(FlightFrame(c,b,0x50,ack,flight)==FlightResult::Continue&&!ack,"Flight2 foreign ACK accepted");
    b[15]=2;b[16]=1;check(FlightFrame(c,b,0x50,ack,flight)==FlightResult::Retry,"Flight2 retry status");
    b[16]=2;check(FlightFrame(c,b,0x50,ack,flight)==FlightResult::Invalid,"Flight2 unknown ACK status");
    ack=false;flight.reset();check(FlightFrame(c,Packet(3,{0x53,1,1}),0x52,ack,flight)==FlightResult::Offline,"Flight2 ordinary offline connection");
    c.revision=0x4110;check(FlightFrame(c,Packet(3,{0x53,1,1}),0x52,ack,flight)==FlightResult::Continue&&flight,"Flight2 documented connection-one exception");
    c=candidate("blackshark2020");b=Packet(64,{255,0x0f,5,0xfe,0x12,4,0x1f,8,5});b[12]=0x0e;b[13]=0x74;b[14]=80;checkValue(c,b,80);b[11]=9;charging=DecodeBattery(c,b);check(charging&&charging->charging==true&&!charging->percentage,"2020 unreliable charging level exposed");b[1]=1;check(!DecodeBattery(c,b),"2020 stale header accepted");
    c=candidate("razer_headset");b=Packet(91,{0,2,0x3f,0,0,0,2,7,0x80,0,153});b[89]=static_cast<unsigned char>(Xor(b,3,89));checkValue(c,b,60);b[2]^=1;check(!DecodeBattery(c,b),"Razer transaction mismatch accepted");
    c=candidate("blackshark_v3_pro");b=Packet(64,{2,2,0x60});b[6]=5;b[9]=128;b[10]=0x21;b[11]=b[12]=1;b[13]=67;b[62]=static_cast<unsigned char>(Xor(b,0,62));checkValue(c,b,67,0x21);b[62]^=1;check(!DecodeBattery(c,b,0x21),"V3 Pro checksum ignored");
    c=candidate("blackshark_v2_hs");b=Packet(64,{2,2,0x60,0,0,0,5,0,0x80,0x80,0x21,1,1,76});b[62]=0x0a;
    checkValue(c,b,76,0x21); // Captured attached-device reply, including checksum.
    b[10]=0x20;check(!DecodeBattery(c,b,0x21),"BlackShark non-battery reply rejected");b[10]=0x21;
    b[1]=3;check(!DecodeBattery(c,b,0x21),"BlackShark error status rejected");
    // 2026-11-11: powered-off 1532:0565, receiver attached. Three identical
    // successful 0020 replies; 8021 remained silent (not offline evidence).
    const auto hsOffline=Packet(64,{2,2,0x60,0,0,0,5,0,0x80,0,0x20,1,1,0});
    b=hsOffline;b[62]=0xc7;
    offline=DecodeBattery(c,b,0x20);
    check(offline&&offline->state=="disconnected"&&!offline->percentage&&!offline->charging,"HS explicit link-down must clear live battery");
    check(!DecodeBattery(c,b,0x21)&&!DecodeBattery(c,b),"HS reply must match requested command");
    check(!DecodeBattery(c,{},0x20)&&!DecodeBattery(c,{},0x21),"HS timeout must remain unknown");
    check(!DecodeBattery(c,Request(c,0x20),0x20),"HS link query echo is not offline");
    for(unsigned at:{0u,1u,2u,3u,4u,5u,6u,7u,8u,9u,10u,11u,12u,14u,61u,62u,63u})
    {auto corrupt=b;corrupt[at]^=1;check(!DecodeBattery(c,corrupt,0x20),"HS corrupt offline frame accepted");}
    auto hsError=b;hsError[1]=3;hsError[62]=static_cast<unsigned char>(Xor(hsError,1,62));
    check(!DecodeBattery(c,hsError,0x20),"HS checksummed query error is not offline");
    auto hsUnknown=b;hsUnknown[13]=2;hsUnknown[62]=static_cast<unsigned char>(Xor(hsUnknown,1,62));
    check(!DecodeBattery(c,hsUnknown,0x20),"HS unknown link enum is not offline");
    auto hsTruncated=b;hsTruncated.pop_back();check(!DecodeBattery(c,hsTruncated,0x20),"HS short link frame accepted");
    auto hsOnline=b;hsOnline[13]=1;hsOnline[62]=static_cast<unsigned char>(Xor(hsOnline,1,62));
    auto link=DecodeBattery(c,hsOnline,0x20);check(link&&link->state=="online","HS synthetic link-up");
    auto hsEmpty=b;hsEmpty[9]=0x80;hsEmpty[10]=0x21;hsEmpty[62]=static_cast<unsigned char>(Xor(hsEmpty,1,62));
    checkValue(c,hsEmpty,0,0x21);check(DecodeBattery(c,hsEmpty,0x21)->state=="online","HS zero battery is not disconnected");
    c=candidate("blackshark_pa");b=Packet(64,{2});b[12]=0x21;b[13]=1;b[14]=1;b[15]=71;checkValue(c,b,71);b[14]=33;check(!DecodeBattery(c,b),"PA payload bounds ignored");
    check(Prefix(Request(c,1),{2,128,7,0,0,0x50,0x41,14,0,2,0xe1,1})&&Request(c,2)[11]==0,"PA temporary read route/restore");
    c=candidate("barracuda_pa");b=Packet(64,{1,128,0,0x50,0x49,8});b[13]=0x21;b[14]=2;b[15]=1;b[16]=59;checkValue(c,b,59);
    c=candidate("hecate_g5max");b=Packet(64,{0xd0,0x35,0xbb,4,0,1,95,2});checkValue(c,b,95);check(!DecodeBattery(c,b)->charging.has_value(),"unknown HECATE charging invented");
    c=candidate("hecate_gx03");b=Packet(64,{0xd0,0x35,0xbb,7,0,3,0x80|80,45});auto ears=DecodeBattery(c,b);
    check(ears&&!ears->percentage&&ears->components.size()==2&&ears->components[0].role=="left"&&ears->components[0].percentage==80&&ears->components[1].role=="right"&&ears->components[1].percentage==45&&!ears->components[0].charging,"GX03 roles or unknown charging lost");
    b[5]=2;ears=DecodeBattery(c,b);check(ears&&ears->components.size()==1&&ears->components[0].role=="right","absent earbud fabricated");
    c=candidate("moondrop_gm");b=Packet(64,{0x4b,0,0x27});b[8]=1;b[9]=92;checkValue(c,b,92,0x27);b[8]=0;check(!DecodeBattery(c,b,0x27),"unavailable GM battery accepted");
    c=candidate("epos_h3pro");b=Packet(19,{1,0xeb,0xfe,1,0});b[9]=6;b[13]=0x12;b[15]=3;b[17]=76;b[11]=static_cast<unsigned char>(Crc8(Bytes(b.begin()+13,b.end())));checkValue(c,b,76);b[11]^=1;check(!DecodeBattery(c,b),"EPOS CRC ignored");
    c=candidate("sony_ds4");b=Packet(64,{1});b[30]=4;checkValue(c,b,45);b[30]=15;check(!DecodeBattery(c,b),"DS4 reserved level accepted");
    check(Request(c).empty(),"DS4 must be passive");
    c=candidate("sony_dualsense");b=Packet(64,{1});b[53]=0x15;charging=DecodeBattery(c,b);check(charging&&charging->percentage==55&&charging->charging==true&&charging->estimated,"DualSense band semantics");
    c.path="bth#vid&054c";c.input=78;c.layout.bytes.clear();c.layout.bytes[{Report::Input,0x31}]=78;check(Admit(c),"DualSense BT profile");b=Packet(78,{0x31});b[54]=4;Bytes data{0xa1};data.insert(data.end(),b.begin(),b.end()-4);unsigned crc=Crc32(data);for(unsigned i=0;i<4;++i)b[74+i]=static_cast<unsigned char>(crc>>(8*i));checkValue(c,b,45);b[75]^=1;check(!DecodeBattery(c,b),"Sony BT CRC ignored");
    c=candidate("eightbitdo_input");b=Packet(64,{4});b[14]=0x80|66;checkValue(c,b,66);b[14]=0;check(!DecodeBattery(c,b),"8BitDo missing battery became zero");
    c=candidate("nintendo_bt");b=Packet(49,{0x30,0,0x50});checkValue(c,b,50);b[2]=0xe0;check(!DecodeBattery(c,b),"Nintendo reserved band accepted");
    c.path="usb#vid_057e";check(!Admit(c),"Nintendo USB admitted");
    c=candidate("flydigi_apex5");b=Packet(32,{4,0x5a,0xa5,1});b[6]=128;b[12]=2;auto bands=DecodeBattery(c,b);check(bands&&bands->levelText=="3/5"&&!bands->percentage,"Apex band semantics");b[6]=130;check(!DecodeBattery(c,b),"Vader model accepted as Apex");
    c=candidate("flydigi_vader5");b=Packet(64,{0x5a,0xa5,1});b[5]=130;b[11]=0x13;b[15]=0x71;b[16]=0x41;bands=DecodeBattery(c,b);check(bands&&bands->levelText=="4/5"&&bands->charging==true,"Vader firmware metadata used as battery gate");
    auto jsonFrame=[](const char* json){size_t n=std::strlen(json);auto v=Packet(64,{0xa5,static_cast<unsigned char>(n+4),0xcc});std::copy(json,json+n,v.begin()+3);v[n+3]=static_cast<unsigned char>(Sum(v,0,n+3));return v;};
    c=candidate("leadjoy_zhiwan");b=jsonFrame("{\"c\":14,\"e\":0,\"r\":{\"mb\":82,\"sb\":255,\"cs\":1}}");checkValue(c,b,82);
    check(!DecodeBattery(c,jsonFrame("{\"c\":14,\"c\":14,\"e\":0,\"r\":{\"mb\":82,\"sb\":0,\"cs\":0}}")),"LeadJoy duplicate key accepted");
    check(!DecodeBattery(c,jsonFrame("{\"c\":14,\"e\":0,\"r\":{\"mb\":true,\"sb\":0,\"cs\":0}}")),"LeadJoy bool battery accepted");
    b.back()^=1;b[2]=0xcd;check(!DecodeBattery(c,b),"LeadJoy wrong envelope accepted");
    auto appReply=[](unsigned command,unsigned seq,Bytes body)
    {Bytes payload{static_cast<unsigned char>(command)};if(command==12)payload.insert(payload.begin(),0xfa);payload.insert(payload.end(),body.begin(),body.end());auto v=Packet(64,{3,0x55,0x80,0,static_cast<unsigned char>(payload.size()+4),0,255,static_cast<unsigned char>(seq),static_cast<unsigned char>(payload.size())});std::copy(payload.begin(),payload.end(),v.begin()+9);v[9+payload.size()]=0xaa;return v;};
    b=appReply(0xef,37,{0x1f,0x38,0x11,0x34});auto identity=IkfApp(b,37,0xef);check(identity&&U16(*identity,0)==0x381f&&U16(*identity,2)==0x3411,"iKF model envelope");
    check(!IkfApp(b,38,0xef),"iKF sequence mismatch accepted");b.back()=1;check(!IkfApp(b,37,0xef),"iKF trailing payload accepted");
    b=appReply(12,38,{80,0,0});auto ikf=IkfApp(b,38,12);check(ikf&&ikf->size()==3&&ikf->at(0)==80,"iKF battery envelope");
    b=Packet(64,{3,0xd6,0xbe,0,1,0,10});unsigned statusCrc=Crc16({1});b[7]=statusCrc&255;b[8]=statusCrc>>8;b[9]=1;check(IkfStatus(b)==1,"iKF link CRC");b[8]^=1;check(!IkfStatus(b),"iKF corrupt link accepted");
    unsigned journals=0,writes=0;bool attempted=false;
    auto initialize=[&](){++journals;if(attempted)return false;attempted=true;return true;};
    auto send=[&](){check(attempted&&journals==1,"write preceded journal");++writes;return false;};
    check(!SendOnce(initialize,send),"initialization failure hidden");check(SendOnce(initialize,send)&&writes==1,"initialization retried after failure");
    check(SendOnce({},send)&&writes==1,"missing initialization authority wrote");
}
}
