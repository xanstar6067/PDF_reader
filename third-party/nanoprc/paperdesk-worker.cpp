// PaperDesk's independent model conversion program. Distributed with source
// under AGPL-3.0, like nanoPRC. Its binary PD3D protocol is documented below.
#include "prc_api.h"
#include <vector>
#include <string>
#include <map>
#include <array>
#include <fstream>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <cmath>
#include <cstring>
#include <algorithm>
using Bytes=std::vector<unsigned char>;
using Matrix=std::array<double,16>;
struct Vertex { float p[3],n[3],c[4]; };
struct Part { std::string name;std::vector<Vertex> vertices; };
static size_t totalVertices=0;
static bool approximatePlacement=false;
static void bounded(size_t count) { if(count>15000000 || totalVertices+count>15000000)throw std::runtime_error("Model exceeds 15 million vertices");totalVertices+=count; }
static Matrix identity(){return {1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};}
static Matrix multiply(const Matrix&a,const Matrix&b){Matrix r{};for(int c=0;c<4;c++)for(int row=0;row<4;row++)for(int k=0;k<4;k++)r[c*4+row]+=a[k*4+row]*b[c*4+k];return r;}
static std::array<float,3> transformPoint(const Matrix&m,const std::array<float,3>&p){return {(float)(m[0]*p[0]+m[4]*p[1]+m[8]*p[2]+m[12]),(float)(m[1]*p[0]+m[5]*p[1]+m[9]*p[2]+m[13]),(float)(m[2]*p[0]+m[6]*p[1]+m[10]*p[2]+m[14])};}
static void triangle(Part&p,std::array<float,3>a,std::array<float,3>b,std::array<float,3>c,std::array<float,4>color){
    for(auto v:{a,b,c})for(float x:v)if(!std::isfinite(x))throw std::runtime_error("Nonfinite model coordinate");
    float u[3]={b[0]-a[0],b[1]-a[1],b[2]-a[2]},v[3]={c[0]-a[0],c[1]-a[1],c[2]-a[2]};
    float n[3]={u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0]};
    double length=std::sqrt((double)n[0]*n[0]+(double)n[1]*n[1]+(double)n[2]*n[2]);
    if(length<1e-20)return;for(auto&x:n)x=(float)(x/length);
    bounded(3);for(auto pos:{a,b,c}){Vertex vertex{};memcpy(vertex.p,pos.data(),12);memcpy(vertex.n,n,12);memcpy(vertex.c,color.data(),16);p.vertices.push_back(vertex);}
}
struct Reader {
    const Bytes&data;size_t pos=0;
    void skip(size_t n){if(n>data.size()-pos)throw std::runtime_error("Truncated U3D");pos+=n;}
    uint32_t u32(){if(data.size()-pos<4)throw std::runtime_error("Truncated U3D");uint32_t n;memcpy(&n,data.data()+pos,4);pos+=4;return n;}
    float f32(){uint32_t n=u32();float f;memcpy(&f,&n,4);if(!std::isfinite(f))throw std::runtime_error("Invalid U3D float");return f;}
    std::string str(){if(data.size()-pos<2)throw std::runtime_error("Truncated U3D string");unsigned n=data[pos]|(data[pos+1]<<8);pos+=2;if(n>data.size()-pos)throw std::runtime_error("Truncated U3D string");std::string s((char*)data.data()+pos,n);pos+=n;return s;}
};
struct Block {uint32_t type;Bytes bytes;std::string chain;};
static void blocks(const Bytes&data,std::vector<Block>&out,int depth=0,const std::string&chain={}) {
    if(depth>32)throw std::runtime_error("U3D hierarchy too deep");size_t p=0;
    while(p+12<=data.size()){
        Reader r{data,p};auto type=r.u32(),size=r.u32(),meta=r.u32();p=r.pos;
        if(size>data.size()-p)throw std::runtime_error("Invalid U3D block length");Bytes payload(data.begin()+p,data.begin()+p+size);
        p+=(size+3ull)&~3ull;if(p>data.size()||meta>data.size()-p)throw std::runtime_error("Invalid U3D metadata");p+=(meta+3ull)&~3ull;
        if(out.size()>100000)throw std::runtime_error("Too many U3D blocks");out.push_back({type,payload,chain});
        if(type==0xffffff14){Reader h{payload};auto name=h.str();h.u32();auto attr=h.u32();if(attr&1)h.skip(16);if(attr&2)h.skip(24);h.pos=(h.pos+3)&~3ull;h.u32();Bytes nested(payload.begin()+h.pos,payload.end());blocks(nested,out,depth+1,name);}
    }
}
struct Node{std::string name,resource,shader;std::vector<std::pair<std::string,Matrix>>parents;};
struct Mesh{bool noNormals=false;struct Shade{bool diffuse,specular;uint32_t layers;};std::vector<Shade>shades;Bytes base;};
static std::vector<Part> readU3d(const Bytes&data){
    std::vector<Block>all;blocks(data,all);std::map<std::string,Mesh>meshes;std::map<std::string,Node>nodes;std::map<std::string,std::array<float,4>>colors;std::map<std::string,std::string>shaders;
    if(data.size()<24)throw std::runtime_error("Invalid U3D header");Reader head{data,16};uint32_t profile=head.u32();
    if(!(profile&4))throw std::runtime_error("Compressed/progressive U3D is not yet supported; use an uncompressed U3D export");
    for(auto&b:all){Reader r{b.bytes};
        if(b.type==0xffffff31){auto name=r.str();auto&m=meshes[name];r.u32();m.noNormals=(r.u32()&1)!=0;for(int i=0;i<6;i++)r.u32();auto n=r.u32();if(n>10000)throw std::runtime_error("Too many U3D materials");for(uint32_t i=0;i<n;i++){auto flags=r.u32(),layers=r.u32();if(layers>8)throw std::runtime_error("Too many texture layers");for(uint32_t j=0;j<layers;j++)r.u32();r.u32();m.shades.push_back({(flags&1)!=0,(flags&2)!=0,layers});}}
        else if(b.type==0xffffff3b){auto name=r.str();meshes[name].base=b.bytes;}
        else if(b.type==0xffffff3c)throw std::runtime_error("Progressive U3D is not yet supported");
        else if(b.type==0xffffff21||b.type==0xffffff22){Node n;n.name=r.str();auto count=r.u32();if(count>100)throw std::runtime_error("Too many node parents");for(uint32_t i=0;i<count;i++){auto parent=r.str();Matrix m{};for(auto&x:m)x=r.f32();n.parents.push_back({parent,m});}if(b.type==0xffffff22)n.resource=r.str();nodes[n.name]=n;}
        else if(b.type==0xffffff54){auto name=r.str();r.u32();r.skip(12);std::array<float,4>c{r.f32(),r.f32(),r.f32(),1};colors[name]=c;}
        else if(b.type==0xffffff53){auto name=r.str();r.skip(28);shaders[name]=r.str();}
        else if(b.type==0xffffff45){r.str();r.u32();r.u32();auto count=r.u32();if(count&&r.pos<b.bytes.size()){auto list=r.u32();if(list&&r.pos<b.bytes.size())nodes[b.chain].shader=r.str();}}
    }
    // Modifier order is not guaranteed: recover shading references after nodes.
    for(auto&b:all)if(b.type==0xffffff45){Reader r{b.bytes};r.str();r.u32();r.u32();auto count=r.u32();if(count){auto list=r.u32();if(list)nodes[b.chain].shader=r.str();}}
    auto world=[&](auto&&self,const std::string&name,int depth)->Matrix{if(depth>64)throw std::runtime_error("Cyclic U3D node graph");auto it=nodes.find(name);if(it==nodes.end()||it->second.parents.empty())return identity();auto&p=it->second.parents.front();return multiply(p.first.empty()?identity():self(self,p.first,depth+1),p.second);};
    std::vector<Part>out;
    for(auto&[name,node]:nodes){if(node.resource.empty())continue;auto it=meshes.find(node.resource);if(it==meshes.end()||it->second.base.empty())continue;auto&m=it->second;Reader r{m.base};r.str();r.u32();uint32_t nf=r.u32(),np=r.u32(),nn=r.u32(),nd=r.u32(),ns=r.u32(),nt=r.u32();if(nf>5000000||np>5000000)throw std::runtime_error("U3D mesh too large");
        std::vector<std::array<float,3>>points;points.reserve(np);auto matrix=world(world,name,0);for(uint32_t i=0;i<np;i++)points.push_back(transformPoint(matrix,{r.f32(),r.f32(),r.f32()}));r.skip((size_t)nn*12+(size_t)(nd+ns+nt)*16);
        Part part;part.name=name;auto material=colors.find(shaders[node.shader]);std::array<float,4>color=material==colors.end()?std::array<float,4>{.65f,.7f,.76f,1}:material->second;
        for(uint32_t i=0;i<nf;i++){auto shade=r.u32();if(shade>=m.shades.size())throw std::runtime_error("Invalid U3D shading index");auto&s=m.shades[shade];std::array<uint32_t,3>idx{};for(auto&j:idx){j=r.u32();if(j>=np)throw std::runtime_error("Invalid U3D vertex index");if(!m.noNormals)r.u32();if(s.diffuse)r.u32();if(s.specular)r.u32();for(uint32_t k=0;k<s.layers;k++)r.u32();}triangle(part,points[idx[0]],points[idx[1]],points[idx[2]],color);}
        if(!part.vertices.empty())out.push_back(std::move(part));
    }return out;
}
static void check(int rc){if(rc)throw std::runtime_error("PRC decoder reported an invalid structure");}
static void prcMesh(prc_context*ctx,prc_api_data data,prc_api_tess&t,Part&part,const Matrix&world){
    auto count=t.type==PRC_API_TESS_3D_Compressed?1:t.num_faces;
    for(size_t f=0;f<count;f++){auto&face=t.tess_faces[f];if(prc_api_skip_face(ctx,&t,(uint32_t)f))continue;auto*buf=t.type==PRC_API_TESS_3D_Compressed?&t.tess_vertices:&face.face_vertices;if(!buf->vertices)continue;
        prc_api_material mat{};prc_api_get_face_material(ctx,&t,&mat,(uint32_t)f);
        std::array<float,4>color{.65f,.70f,.76f,1};if(prc_api_face_is_material(ctx,&t,(uint32_t)f))for(int j=0;j<3;j++)color[j]=std::clamp(mat.diffuse[j],0.f,1.f);
        auto emit=[&](uint32_t a,uint32_t b,uint32_t c){if(a>=buf->num_vertices||b>=buf->num_vertices||c>=buf->num_vertices)throw std::runtime_error("Invalid PRC triangle index");std::array<float,3>p[3];uint32_t indices[]={a,b,c};for(int j=0;j<3;j++){memcpy(p[j].data(),buf->vertices[indices[j]].position,12);p[j]=transformPoint(world,p[j]);}auto clr=color;auto&v=buf->vertices[a];if(v.tri_has_material)for(int j=0;j<3;j++)clr[j]=std::clamp(v.diffuse[j],0.f,1.f);triangle(part,p[0],p[1],p[2],clr);};
        for(size_t i=0;i<face.num_graphic_primitives;i++){prc_api_graphic_primitive g{};check(prc_api_get_graphics_primitive(ctx,data,&t,(uint32_t)f,i,&g));
            if(g.type==PRC_API_TRIANGLES)for(size_t j=0;j+2<g.num_indices;j+=3)emit(g.indices[j],g.indices[j+1],g.indices[j+2]);
            else if(g.type==PRC_API_STRIP)for(size_t j=0;j+2<g.num_indices;j++)if(j%2)emit(g.indices[j+1],g.indices[j],g.indices[j+2]);else emit(g.indices[j],g.indices[j+1],g.indices[j+2]);
            else if(g.type==PRC_API_FAN)for(size_t j=1;j+1<g.num_indices;j++)emit(g.indices[0],g.indices[j],g.indices[j+1]);
        }
    }
}
static Matrix checkedMatrix(const prc_api_transform&transform){
    Matrix result{};for(int i=0;i<16;i++){if(!std::isfinite(transform.matrix[i]))throw std::runtime_error("Invalid PRC transform");result[i]=transform.matrix[i];}
    if(std::abs(result[3])>1e-12||std::abs(result[7])>1e-12||std::abs(result[11])>1e-12||std::abs(result[15]-1)>1e-12)throw std::runtime_error("Projective PRC transform is not supported");return result;
}
static std::vector<Part>readPrc(const char*path){
    auto ctx=prc_api_new_context(nullptr);if(!ctx)throw std::runtime_error("Cannot create PRC decoder");auto data=prc_api_open_contents(ctx,path);if(!data)throw std::runtime_error("Cannot parse PRC stream");
    uint32_t parts=0,products=0,markups=0,nt=0,nl=0,ne=0;prc_api_product*tree=nullptr;
    check(prc_api_prep_model_tree(ctx,data,&parts,&products,&markups));check(prc_api_create_model_tree(ctx,data,&tree,parts,products,markups));check(prc_api_get_number_tessellations(ctx,data,tree,&nt,&nl,&ne,0));if(nt>10000)throw std::runtime_error("Too many PRC meshes");
    std::vector<prc_api_tess>tesses(nt);std::vector<Part>out;
    for(uint32_t k=0;k<nt;k++){auto&t=tesses[k];t.num_faces=prc_api_get_number_faces(ctx,data,k);if(t.num_faces>1000000)throw std::runtime_error("Too many PRC faces");t.tess_faces=(prc_api_face*)calloc(std::max<size_t>(1,t.num_faces),sizeof(prc_api_face));uint8_t lines=0;check(prc_api_initialize_tessellation(ctx,data,tree,k,&t,nullptr,&lines));if(t.type!=PRC_API_TESS_3D&&t.type!=PRC_API_TESS_3D_Compressed)continue;
        for(uint32_t j=0;j<t.num_faces;j++)check(prc_api_get_tessellation_vertices(ctx,data,k,j,t.tess_faces+j,&t));
    }
    // Assemble occurrences, not a flat list of resources. part_detail_index is
    // the library's validated global (file structure, tessellation, style) map.
    // File-local biased_tess_index MUST NOT index tesses directly.
    std::vector<bool>visited(nt,false);size_t traversed=0;
    auto walkPart=[&](auto&&self,prc_api_part*part,const Matrix&parent,const std::string&instance,int depth)->void{
        if(!part)return;if(depth>128||++traversed>100000)throw std::runtime_error("PRC representation tree too large");
        prc_api_transform local{};check(prc_api_part_local_transform(ctx,data,part,&local));auto world=multiply(parent,checkedMatrix(local));
        if(part->biased_tess_index){auto index=part->part_detail_index;if(index>=nt)throw std::runtime_error("Invalid global PRC resource reference");auto&t=tesses[index];visited[index]=true;if(t.type==PRC_API_TESS_3D||t.type==PRC_API_TESS_3D_Compressed){Part output;output.name=instance+(part->name&&*part->name?" / "+std::string(part->name):"");prcMesh(ctx,data,t,output,world);if(!output.vertices.empty())out.push_back(std::move(output));}}
        for(size_t i=0;i<part->num_rep_items;i++)self(self,part->rep_items+i,world,instance,depth+1);
    };
    auto walkProduct=[&](auto&&self,prc_api_product*node,const Matrix&parent,const std::string&path,int depth)->void{
        if(!node||depth>128||++traversed>100000)throw std::runtime_error("PRC occurrence tree too large");
        auto world=node->location_set?multiply(parent,checkedMatrix(node->location)):parent;
        auto name=node->name&&*node->name?std::string(node->name):"Assembly";auto instance=path.empty()?name:path+" / "+name;
        walkPart(walkPart,node->part,world,instance,0);for(size_t i=0;i<node->num_children;i++)self(self,node->children+i,world,instance,depth+1);
    };
    if(tree)walkProduct(walkProduct,tree,identity(),"",0);
    for(uint32_t i=0;i<nt;i++)if(!visited[i]&&(tesses[i].type==PRC_API_TESS_3D||tesses[i].type==PRC_API_TESS_3D_Compressed)){
        approximatePlacement=true;Part fallback;fallback.name="Unplaced resource "+std::to_string(i+1);prcMesh(ctx,data,tesses[i],fallback,identity());if(!fallback.vertices.empty())out.push_back(std::move(fallback));
    }
    prc_api_release_data(ctx,data,tesses.data(),nt,nullptr,0,nullptr,0,tree);prc_api_release_context(ctx);return out;
}
// PD3D0001, uint32 flags, uint32 part count; per part: UTF8 name byte count,
// name, uint32 vertex count, packed 40-byte position/normal/RGBA vertices.
int main(int argc,char**argv){try{
    if(argc!=3)return 2;std::ifstream input(argv[1],std::ios::binary);if(!input)throw std::runtime_error("Cannot open model stream");input.seekg(0,std::ios::end);auto size=input.tellg();if(size<3||size>256*1024*1024)throw std::runtime_error("Invalid model stream size");input.seekg(0);Bytes bytes((size_t)size);input.read((char*)bytes.data(),size);
    bool prc=bytes[0]=='P'&&bytes[1]=='R'&&bytes[2]=='C';auto parts=prc?readPrc(argv[1]):readU3d(bytes);if(parts.empty())throw std::runtime_error("No supported triangle geometry in the model");
    std::ofstream output(argv[2],std::ios::binary);if(!output)throw std::runtime_error("Cannot open mesh output");output.write("PD3D0001",8);uint32_t flags=approximatePlacement?1:0,count=(uint32_t)parts.size();output.write((char*)&flags,4);output.write((char*)&count,4);
    for(auto&p:parts){uint32_t len=(uint32_t)p.name.size(),n=(uint32_t)p.vertices.size();output.write((char*)&len,4);output.write(p.name.data(),len);output.write((char*)&n,4);output.write((char*)p.vertices.data(),(std::streamsize)n*sizeof(Vertex));}
    if(!output)throw std::runtime_error("Cannot write mesh output");std::cout<<parts.size()<<" parts, "<<totalVertices/3<<" triangles\n";return 0;
}catch(const std::exception&e){std::cerr<<e.what()<<'\n';return 1;}}
