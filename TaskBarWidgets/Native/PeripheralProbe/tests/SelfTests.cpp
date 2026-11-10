#include "SelfTests.h"
#include "../ExtraKeyboard.h"
#include "../KeyboardAdvanced.h"
#include "../HeadsetController.h"
#include "../ExtraMouse.h"
#include "../Protocol.h"
#include <iostream>
#include <stdexcept>
using namespace peripheral;
namespace
{
void Check(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}
Candidate Mouse(Protocol protocol)
{
    Candidate c; c.protocol = protocol; c.vid = 0x3554; c.pid = 0xfb3e; c.mi = 1; c.page = 0xff02; c.usage = 2;
    c.pointer = true; c.path = "synthetic-channel"; c.physical = "synthetic-container";
    c.input = c.output = 17;
    c.layout.valid = true; c.layout.bytes[{Report::Input,8}] = 17; c.layout.bytes[{Report::Output,8}] = 17;
    return c;
}
void Tests()
{
    {
        std::set<std::string> journal;
        unsigned events = 0;
        for (unsigned i = 0; i < 4095; ++i) journal.insert("attempt-" + std::to_string(i));
        Check(BeginInitialization(journal, "last", [&] { Check(journal.count("last") == 1, "journal precedes initialized event"); ++events; }), "last journal slot");
        Check(!BeginInitialization(journal, "last", [&] { ++events; }), "existing initialization never retries");
        Check(!BeginInitialization(journal, "overflow", [&] { ++events; }), "full initialization journal refuses a new identity");
        Check(events == 1 && journal.size() == 4096 && !journal.count("overflow"), "full journal emits no unremembered event");
    }
    Bytes descriptor{0x06,0x02,0xff,0x09,2,0xa1,1,0x85,8,0x75,8,0x95,16,0x81,2,0x91,2,0xc0};
    auto layout = ParseDescriptor(descriptor);
    Check(layout.Has(Report::Input,8,17) && layout.Has(Report::Output,8,17), "numbered report geometry");
    descriptor.pop_back(); Check(!ParseDescriptor(descriptor).valid,"unclosed collection");
    Check(!ParseDescriptor({0x76,0xff}).valid,"truncated item");
    Check(!ParseDescriptor({0xb4}).valid,"global stack underflow");
    {
        // Actual Windows descriptors captured from the attached devices on 2026-11-10.
        auto capturedLayout = [](const std::string& hex) {
            Bytes b; for (size_t i = 0; i < hex.size(); i += 2) b.push_back(static_cast<unsigned char>(std::stoul(hex.substr(i, 2), nullptr, 16)));
            return ParseDescriptor(b);
        };
        Candidate logi; logi.path="long-collection";logi.physical="receiver";logi.vid=0x046d;logi.pid=0xc547;
        logi.mi=2;logi.page=0xff00;logi.usage=2;logi.input=logi.output=20;
        logi.layout=capturedLayout("0600ff0902a10185110902150026ff007508951381000902150026ff00750895139100c0");
        Check(Classify(logi)==Protocol::Hidpp,"C547 split long collection supports HID++ queries");
        auto shortOnly=logi;shortOnly.usage=1;shortOnly.input=shortOnly.output=7;shortOnly.layout.bytes.clear();
        shortOnly.layout.bytes[{Report::Input,16}]=7;shortOnly.layout.bytes[{Report::Output,16}]=7;
        Check(Classify(shortOnly)==Protocol::None,"C547 short-only collection cannot receive unit identity");
        auto unrelated=logi;unrelated.pid=0xbeef;
        Check(Classify(unrelated)==Protocol::None,"long geometry does not grant arbitrary receivers");

        Candidate razer;razer.path="battery-collection";razer.physical="headset";razer.vid=0x1532;razer.pid=0x0565;
        razer.mi=3;razer.page=0xff14;razer.usage=1;razer.input=razer.output=64;razer.feature=9;
        razer.layout=capturedLayout("0614ff0901a10185020900150026ff007508953f81020900150026ff007508953f91020900150026ff0075089508b20201c0");
        Check(Classify(razer)==Protocol::HeadsetController&&razer.adapter=="blackshark_v2_hs","BlackShark battery I/O coexists with Feature9");
        razer.layout.bytes.erase({Report::Output,2});
        Check(Classify(razer)==Protocol::None,"BlackShark missing battery output rejected");

        Candidate wireless;wireless.path="azoth-wireless";wireless.physical="wireless-container";
        wireless.vid=0x0b05;wireless.pid=0x1a85;wireless.mi=1;wireless.page=0xff00;wireless.usage=1;
        wireless.input=wireless.output=65;wireless.serial="unique-usb-serial";
        wireless.layout=capturedLayout("0600ff0901a1010902150026ff007508954081020903150026ff00750895409102c0");
        Check(Classify(wireless)==Protocol::ExtraKeyboard,"actual Azoth wireless descriptor");
        auto wired=wireless;wired.pid=0x1a83;wired.path="azoth-wired";wired.physical="wired-container";
        Check(Classify(wired)==Protocol::ExtraKeyboard&&wired.wired,"Azoth wired reuses battery-only protocol");
        Check(CanonicalIdentity(wired)==CanonicalIdentity(wireless),"same proven Azoth serial survives transport change");
        Check(CanonicalIdentity(wireless).find("|pid=1a85|")!=std::string::npos,"retain historically supported Azoth model PID");
        wireless.hidSerial="unique-usb-serial\x1b";
        auto aliases=LegacyIdentities(wireless);auto old=wireless;old.serial=wireless.hidSerial;
        Check(aliases.size()==1&&aliases[0]==CanonicalIdentity(old)&&aliases[0]!=CanonicalIdentity(wireless),"exact observed raw HID serial supplies legacy key");
        auto unproven=wireless;unproven.serial.clear();
        Check(LegacyIdentities(unproven).empty(),"no legacy alias without authoritative identity");
        unproven=wireless;unproven.hidSerial=unproven.serial;
        Check(LegacyIdentities(unproven).empty(),"do not emit canonical identity as its own alias");
        std::vector<Candidate> pair{wireless,wired};SelectUnique(pair);
        Check(pair[0].protocol==Protocol::None&&pair[1].protocol==Protocol::ExtraKeyboard,"Azoth pair prefers wired once");
        Check(LegacyIdentities(pair[1])==aliases&&LegacyIdentities(pair[0]).empty(),"preferred wired candidate carries exact proven receiver alias");
        wired.serial="other-usb-serial";pair={wireless,wired};SelectUnique(pair);
        Check(pair[0].protocol==Protocol::ExtraKeyboard&&pair[1].protocol==Protocol::ExtraKeyboard&&pair[0].identity!=pair[1].identity,"different Azoth units never merge");
        Check(LegacyIdentities(pair[1]).empty(),"different serial never transfers receiver aliases");
        wired.serial.clear();wireless.serial.clear();pair={wireless,wired};SelectUnique(pair);
        Check(pair[0].protocol==Protocol::ExtraKeyboard&&pair[1].protocol==Protocol::ExtraKeyboard&&pair[0].identity!=pair[1].identity,"missing serial does not prove Azoth pair");
    }
    auto c = Mouse(Protocol::Compx);
    Check(Classify(c) == Protocol::Compx,"finite COMPX match");
    auto privateMouse = c; privateMouse.pid = 0xf517;
    Check(Classify(privateMouse) == Protocol::ExtraMouse && privateMouse.adapter == "x23_compx", "shared X23 receiver requires model proof");
    auto privateKeyboard = c; privateKeyboard.vid = 0x3151; privateKeyboard.pid = 0x5026; privateKeyboard.mi = 2;
    privateKeyboard.page = 0xffff; privateKeyboard.usage = 2; privateKeyboard.input = privateKeyboard.output = 0; privateKeyboard.feature = 65;
    privateKeyboard.layout.bytes.clear(); privateKeyboard.layout.bytes[{Report::Feature,0}] = 64;
    privateKeyboard.keyboard = true;
    Check(Classify(privateKeyboard) == Protocol::KeyboardAdvanced && privateKeyboard.adapter == "keycool_k19", "physical keyboard presence does not reject a vendor collection");
    auto unknown = c; unknown.pid = 0xbeef;
    auto t7 = c; t7.vid=0x36f9;t7.pid=0xf008;t7.mi=0;t7.page=0xff90;t7.usage=1;t7.input=t7.output=64;
    t7.layout.bytes.clear();t7.layout.bytes[{Report::Input,0x55}]=64;t7.layout.bytes[{Report::Output,0x55}]=64;
    Check(Classify(t7)==Protocol::Xiberia,"T7 requires defined input/output55");
    t7.layout.bytes.erase({Report::Output,0x55});Check(Classify(t7)==Protocol::None,"T7 cannot use input-only collection");
    auto mg68=c;mg68.vid=0x3151;mg68.pid=0x5038;mg68.mi=2;mg68.page=0xffff;mg68.usage=2;mg68.feature=65;
    mg68.layout.bytes.clear();mg68.layout.bytes[{Report::Feature,0}]=64;
    Check(Classify(mg68)==Protocol::Irok,"MG68 explicit unnumbered feature");
    Bytes status(65);status[2]=67;auto mg68Reading=Decode(mg68,status);
    Check(mg68Reading&&mg68Reading->percentage==67&&!Decode(mg68,BatteryQuery(mg68).packet),"MG68 percent and echo rejection");
    Check(Classify(unknown) == Protocol::None,"no family-wide PID grants");
    auto wrong = c; wrong.layout.bytes[{Report::Output,8}] = 18;
    Check(Classify(wrong) == Protocol::None,"report capability gate");
    std::vector<Candidate> duplicates{c,c}; duplicates[1].path = "second-channel";
    SelectUnique(duplicates);
    Check(duplicates[0].protocol == Protocol::None && duplicates[1].protocol == Protocol::None,"ambiguous channel rejected");
    auto copy = c; copy.mi = 2;
    Check(CanonicalIdentity(c) != CanonicalIdentity(copy),"interface identity");
    copy = c; copy.physical = "other-container";
    Check(CanonicalIdentity(c) != CanonicalIdentity(copy),"receiver identity");
    c.serial="unique-device-serial";copy=c;copy.path="different-port-collection";copy.physical="different-port-container";
    Check(CanonicalIdentity(c)==CanonicalIdentity(copy),"serial identity survives port changes");c.serial.clear();
    auto query = BatteryQuery(c);
    Check(query.packet.size() == 17 && query.packet[16] == 0x49,"COMPX immutable query");
    Check(!Decode(c,query.packet),"COMPX echo rejected");
    Bytes b(17); b[0]=8; b[1]=4; b[2]=1; b[6]=67; b[7]=1;
    b[16] = static_cast<unsigned char>(0x55-8-4-1-67-1);
    auto r=Decode(c,b); Check(r && r->percentage==67 && r->charging==true,"COMPX battery checksum");
    b[6]=101; Check(!Decode(c,b),"invalid percentage/checksum");
    c.protocol=Protocol::Nordic; b.assign(64,0); b[0]=8;b[1]=0x72;b[5]=7;b[6]=1;b[7]=0;
    r=Decode(c,b); Check(r && r->percentage==0 && !r->charging,"zero is distinct from missing, charging unknown");
    b[2]=1;r=Decode(c,b);Check(r && !r->percentage && r->state=="sleeping","Nordic sleep");
    c.protocol=Protocol::Sonix;b.assign(65,0);b[1]=1;b[2]=8;b[3]=0x81;b[4]=1;b[7]=75;
    r=Decode(c,b);Check(r && r->percentage==75,"Sonix register correlation");
    Check(!Decode(c,b,1),"Sonix wrong register rejected");
    c.protocol=Protocol::Incott;b.assign(8,0);b[0]=9;b[1]=0x80|40;
    r=Decode(c,b);Check(r && r->percentage==40 && r->charging==true,"passive charging bit");
    Check(BatteryQuery(c).packet.empty(),"passive sends no packet");
    c.protocol=Protocol::Steelseries;b={0xd2,0x80|11};r=Decode(c,b);
    Check(r && r->percentage==50 && r->estimated && r->charging==true,"Aerox bands stay estimated");
    c.protocol=Protocol::Blackshark;Check(OnceOnly(c.protocol),"once-only classification");
    b.assign(64,0);b[0]=2;b[10]=0x21;b[11]=2;b[12]=1;b[13]=0;
    r=Decode(c,b);Check(r && r->percentage==0,"BlackShark fresh zero");
    b[12]=2;Check(!Decode(c,b),"BlackShark payload framing");
    c.protocol=Protocol::Razer;c.transaction=0x1f;
    b.assign(91,0);b[1]=2;b[2]=0x1f;b[6]=2;b[7]=7;b[8]=0x80;b[10]=128;
    for(unsigned i=3;i<89;++i)b[89]^=b[i];
    r=Decode(c,b);Check(r && r->percentage==50 && !r->charging,"Razer transaction checksum");
    b[2]=0xff;Check(!Decode(c,b),"Razer wrong transaction");
    r=DecodeHidppBattery(0x1001,{0x0e,0xe3,0});
    Check(r && r->estimated && r->percentage==50,"HID++ voltage estimate");
    r=DecodeHidppBattery(0x1004,{0,0,0,0},true);
    Check(r&&r->percentage==0,"HID++ precise true zero supported");
    r=DecodeHidppBattery(0x1004,{0,2,0,0},false);
    Check(r&&!r->percentage&&r->levelText=="low","HID++ bands retain original meaning");
    r=DecodeHidppBattery(0x1000,{0,0,0});
    Check(r&&!r->percentage,"HID++ 1000 reserved zero is unknown");
    r=DecodeHidppBattery(0x1004,{50,0,4,0});
    Check(r&&!r->charging,"HID++ charging error is unknown, never charging");
    Bytes unitInfo{2,1,2,3,4,0,4,0x40,0x82,0,0,0,0};
    auto unit=HidppUnitIdentity(unitInfo);Check(unit=="01020304|model=408200000000","HID++ hardware unit identity");
    Check(!ValidHidppUnitIdentity("00000000|model=408200000000"),"HID++ placeholder unit blocked");
    c.protocol=Protocol::Hidpp;c.unit=unit;copy=c;copy.unit="05060708|model=408200000000";
    Check(CanonicalIdentity(c)!=CanonicalIdentity(copy),"paired replacement gets distinct stable identity");c.unit.clear();
    // Replay multiple paired devices, slot changes, duplicate IDs and pairing races
    // through the actual routing engine. The injected transport never opens hardware.
    Candidate receiver=c;receiver.unit.clear();receiver.identity=CanonicalIdentity(receiver);
    std::map<unsigned,unsigned> peers{{1,0x11},{2,0x22}};
    std::map<unsigned,unsigned> infoCalls;bool changePairing=false;
    HidppCall fake=[&](unsigned slot,unsigned feature,unsigned function,Bytes params)->Bytes{
        if(!peers.count(slot))return {};
        if(feature==0&&function==1)return {2,0,params[2]};
        if(feature==0&&function==0)
        {
            unsigned id=(unsigned(params[0])<<8)|params[1];
            return {static_cast<unsigned char>(id==3?1:id==5?2:id==0x1004?3:0),0,0};
        }
        if(feature==2&&function==2)return {static_cast<unsigned char>(slot==1?3:0)};
        if(feature==1&&function==0)
        {
            Bytes body{2,1,2,3,static_cast<unsigned char>(peers[slot]),0,4,0x40,0x82,0,0,0,0};
            if(changePairing&&++infoCalls[slot]>=3)body[4]^=0x80;
            return body;
        }
        if(feature==3&&function==0)return {15,2};
        if(feature==3&&function==1)return {static_cast<unsigned char>(peers[slot]),0,0,0};
        return {};
    };
    unsigned batteryQueries=0;
    HidppCall identifyOnly=[&](unsigned slot,unsigned feature,unsigned function,Bytes params)->Bytes{
        if(feature==3||(feature==0&&function==0&&params.size()>=2&&params[0]==0x10))++batteryQueries;
        return fake(slot,feature,function,std::move(params));
    };
    auto identified=ReadHidppUnits(receiver,identifyOnly,false);
    Check(identified.size()==2&&batteryQueries==0&&!identified[0].second&&!identified[1].second,"HID++ identify never queries any battery feature");
    auto units=ReadHidppUnits(receiver,fake);
    Check(units.size()==2&&units[0].first.identity!=units[1].first.identity,"HID++ multi-slot units are independent");
    std::string firstIdentity=units[0].first.identity;
    std::swap(peers[1],peers[2]);units=ReadHidppUnits(receiver,fake);
    Check(units.size()==2&&units[1].first.identity==firstIdentity,"HID++ slot reassignment preserves unit identity");
    Candidate selected=units[1].first;units=ReadHidppUnits(selected,fake);
    Check(units.size()==1&&units[0].first.identity==firstIdentity,"HID++ resolves only requested hardware unit");
    peers[1]=0x33;peers[2]=0x44;Check(ReadHidppUnits(selected,fake).empty(),"paired replacement cannot inherit old selection");
    peers[1]=peers[2]=0x55;Check(ReadHidppUnits(receiver,fake).empty(),"duplicate hardware unit identities are blocked");
    peers={{1,0x11}};changePairing=true;infoCalls.clear();units=ReadHidppUnits(receiver,fake);
    Check(units.size()==1&&!units[0].second,"HID++ pairing change invalidates battery reply");
    {
        changePairing=false;infoCalls.clear();
        const std::string modelName="G502 X LIGHTSPEED";
        unsigned nameCalls=0;bool failedChunk=false,failedNameCheck=false,oversized=false;
        HidppCall named=[&](unsigned slot,unsigned feature,unsigned function,Bytes params)->Bytes{
            if(feature==2&&function==0){++nameCalls;return {static_cast<unsigned char>(oversized?65:modelName.size())};}
            if(feature==2&&function==1)
            {
                ++nameCalls;if(failedChunk)return {};
                size_t offset=params[0],end=std::min(modelName.size(),offset+16);
                return Bytes(modelName.begin()+offset,modelName.begin()+end);
            }
            if(failedNameCheck&&nameCalls&&feature==1&&function==0)return {};
            return fake(slot,feature,function,std::move(params));
        };
        std::map<std::string,std::string> names;
        auto namedUnits=ReadHidppUnits(receiver,named,true,&names);
        Check(namedUnits.size()==1&&namedUnits[0].first.name==modelName&&namedUnits[0].first.kind=="mouse"&&namedUnits[0].second&&namedUnits[0].second->percentage==0x11,"HID++ count/chunks supply real name without changing battery/type");
        Check(nameCalls==3&&names.size()==1,"HID++ name offsets advance across long report chunks");
        auto namedIdentity=namedUnits[0].first.identity;
        namedUnits=ReadHidppUnits(receiver,named,true,&names);
        Check(nameCalls==3&&namedUnits[0].first.identity==namedIdentity&&namedUnits[0].first.name==modelName,"confirmed unit/model name cache avoids repeated queries");
        names.clear();nameCalls=0;failedChunk=true;
        namedUnits=ReadHidppUnits(receiver,named,true,&names);
        Check(names.empty()&&namedUnits[0].second&&namedUnits[0].first.name=="Logitech HID++ mouse","optional name timeout preserves fresh battery and generic fallback");
        failedChunk=false;failedNameCheck=true;nameCalls=0;
        namedUnits=ReadHidppUnits(receiver,named,true,&names);
        Check(names.empty()&&namedUnits[0].second,"optional name identity recheck failure cannot discard confirmed battery");
        failedNameCheck=false;oversized=true;nameCalls=0;
        namedUnits=ReadHidppUnits(receiver,named,true,&names);
        Check(nameCalls==1&&names.empty()&&namedUnits[0].second,"oversized name count never causes unbounded metadata queries");
    }
    Check(HidppReplyMatches({0x10,1,4,0x19,90,0,0},1,4,0x19),"HID++ address correlation");
    Check(!HidppReplyMatches({0x10,1,4,0x18,90,0,0},1,4,0x19),"HID++ stale tag rejected");
    c.protocol=Protocol::Gaia;r=Decode(c,{0xff,1,0,1,0x2e,0x50,0x23,6,42});
    Check(r&&r->percentage==42,"GAIA complete frame");
    Check(!Decode(c,{0xff,1,0,2,0x2e,0x50,0x23,6,42}),"GAIA length rejects truncation");
    c.protocol=Protocol::N9Serial;b.assign(64,0);b[0]=4;b[2]=3;b[4]=0x80|61;
    r=Decode(c,b);Check(r&&r->percentage==61&&r->charging==true,"N9 serial status");
    c.protocol=Protocol::OHub;Check(!Decode(c,{0x87,0}),"O HUB missing is not zero");
    r=Decode(c,{0x87,80});Check(r&&r->percentage==80&&!r->charging,"O HUB percent only");
    Check(SonyRequest(3,2).empty(),"Sony refuses non-GET command");
    b={4,0xff,11,0,0x96,0xc3,0x14,4,0x10,0x34,0x12,1,70,0};
    for(size_t i=3;i+1<b.size();++i)b.back()=static_cast<unsigned char>(b.back()+b[i]);
    auto payload=SonyPayload(b,4,0x1234);Check(payload&&*payload==Bytes({1,70}),"Sony correlated checksum");
    Check(!SonyPayload(b,4,0x1235),"Sony stale transaction");
    c.protocol=Protocol::F87Passive;b.assign(20,0);b[0]=0x13;b[1]=0xa;b[2]=1;b[4]=4;b[5]=5;b[6]=72;
    for(unsigned i=0;i<19;++i)b[19]=static_cast<unsigned char>(b[19]+b[i]);
    r=Decode(c,b);Check(r&&r->percentage==72&&!r->charging,"FA09 battery event checksum");
    b[5]=2;Check(!Decode(c,b),"FA09 connection event is not battery");
    // Every decoder rejects every truncated packet; random bytes never escape bounds.
    for(auto protocol : {Protocol::Compx,Protocol::Nordic,Protocol::Razer,Protocol::Rog,Protocol::RogStandard,
        Protocol::Mhub,Protocol::MhubV2,Protocol::Sonix,Protocol::Incott,Protocol::Rapoo,Protocol::X23,
        Protocol::X23Passive,Protocol::Steelseries,Protocol::Blackshark,Protocol::Xiberia,Protocol::N9Serial,
        Protocol::OHub,Protocol::Gaia,Protocol::F87Passive,Protocol::DeluxPassive,Protocol::Rainy75,Protocol::Irok,Protocol::Langtu,Protocol::Pelta})
    {
        c.protocol=protocol;
        for(unsigned size=0;size<=130;++size)
        {
            Bytes invalid(size,0xff);Check(!Decode(c,invalid),"malformed/truncated decoder frame");
        }
    }
}
}
int RunPeripheralSelfTests()
{
    try { Tests(); peripheral::extra_keyboard::OfflineSelfTests(); peripheral::keyboard_advanced::OfflineSelfTests(); peripheral::headset_controller::OfflineSelfTests(); peripheral::extra_mouse::OfflineSelfTests(); std::cout << "Peripheral probe offline selftests passed\n"; return 0; }
    catch(const std::exception& e) { std::cerr << "Peripheral probe selftest failed: " << e.what() << '\n'; return 1; }
}
