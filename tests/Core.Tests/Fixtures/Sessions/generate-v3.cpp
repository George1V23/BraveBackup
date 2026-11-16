// Independent synthetic encoder, derived from pinned Chromium source definitions.
// No Chromium code is linked. These fixtures are not captured personal browser data.
#include <cstdint>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>
#include <filesystem>
using Bytes=std::vector<uint8_t>;
template<typename T> void append(Bytes& b,T v){auto p=reinterpret_cast<const uint8_t*>(&v);b.insert(b.end(),p,p+sizeof(v));}
struct Pickle {
 Bytes b=Bytes(4);
 template<typename T> void value(T v){append(b,v);while(b.size()%4)b.push_back(0);}
 void text(const std::string& v){value<int32_t>(v.size());b.insert(b.end(),v.begin(),v.end());while(b.size()%4)b.push_back(0);}
 void text16(const std::u16string& v){value<int32_t>(v.size());auto p=reinterpret_cast<const uint8_t*>(v.data());b.insert(b.end(),p,p+v.size()*2);while(b.size()%4)b.push_back(0);}
 Bytes finish(){uint32_t n=b.size()-4;std::memcpy(b.data(),&n,4);return b;}
};
struct Closed {int32_t id;int64_t time;};
struct Group {int32_t tab;uint64_t high,low;bool has;};
struct Pinned {int32_t tab;bool value;};
static_assert(sizeof(Closed)==16 && sizeof(Group)==32 && sizeof(Pinned)==8);
void command(Bytes& file,uint8_t id,const Bytes& data){append<uint16_t>(file,data.size()+1);file.push_back(id);file.insert(file.end(),data.begin(),data.end());}
template<typename T> void raw(Bytes& file,uint8_t id,T data){Bytes b;append(b,data);command(file,id,b);}
void pair(Bytes& f,uint8_t id,int32_t a,int32_t b){Bytes data;append(data,a);append(data,b);command(f,id,data);}
void nav(Bytes& f,int tab,int index,const std::string& url,const std::u16string& title){
 Pickle p;p.value<int32_t>(tab);p.value<int32_t>(index);p.text(url);p.text16(title);p.text("excluded-page-state-marker");p.value<int32_t>(0);
 p.value<int32_t>(0);p.text("https://excluded-referrer.test/");p.value<int32_t>(0);p.text("");p.value<uint8_t>(0);p.value<int64_t>(0);p.text16(u"");p.value<int32_t>(200);p.value<int32_t>(0);p.value<int32_t>(0);for(int i=0;i<3;i++)p.value<int64_t>(-1);p.value<int32_t>(0);command(f,6,p.finish());
}
Bytes header(){Bytes f;append<uint32_t>(f,0x53534E53);append<int32_t>(f,3);return f;}
void save(const std::filesystem::path& p,const Bytes& f){std::ofstream out(p,std::ios::binary);out.write(reinterpret_cast<const char*>(f.data()),f.size());}
int main(int argc,char**argv){
 auto dir=std::filesystem::path(argc>1?argv[1]:".");std::filesystem::create_directories(dir);auto f=header();
 pair(f,9,1,0);pair(f,9,2,1);pair(f,0,1,11);pair(f,2,11,0);nav(f,11,0,"https://old.example.test/",u"History, excluded");nav(f,11,1,"https://selected.example.test/",u"Selected ☃");nav(f,11,2,"https://forward.example.test/",u"Forward, excluded");pair(f,7,11,1);
 Pinned pin{};pin.tab=11;pin.value=true;raw(f,12,pin);
 Group group{};group.tab=11;group.high=0x1122334455667788;group.low=0x99aabbccddeeff00;group.has=true;raw(f,25,group);
 Pickle meta;meta.value<uint64_t>(group.high);meta.value<uint64_t>(group.low);meta.text16(u"Group Îñ");meta.value<uint32_t>(6);meta.value<uint8_t>(1);meta.value<uint8_t>(0);command(f,27,meta.finish());
 pair(f,0,1,12);pair(f,2,12,1);nav(f,12,0,"https://second.example.test/",u"Second");pair(f,7,12,0);pair(f,8,1,1);
 pair(f,0,1,13);pair(f,2,13,2);nav(f,13,0,"https://closed.example.test/",u"Closed");
 pair(f,0,2,21);pair(f,2,21,0);nav(f,21,0,"https://popup.example.test/",u"Popup");pair(f,8,2,0);raw<int32_t>(f,20,1);
 command(f,255,{});Closed closed{};closed.id=13;raw(f,16,closed);
 // Move tab 12 into popup window and change selection after the initial marker.
 pair(f,0,2,12);pair(f,2,12,1);pair(f,8,2,1);
 save(dir/"Session_10",f);auto empty=header();command(empty,255,{});save(dir/"Session.empty-v3.bin",empty);
 auto encrypted=header();encrypted[4]=5;save(dir/"Session.encrypted-header.bin",encrypted);
 auto missing=f;missing.resize(missing.size()-1);save(dir/"Session.truncated.bin",missing);
 auto unsupported=f;command(unsupported,254,{});save(dir/"Session.unknown-command.bin",unsupported);
}
