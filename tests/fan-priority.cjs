// Exercises the real fan without browser networking or an external player.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');

function environment() {
  const images=[], requests=[], navigation=[], timers=new Map(); let timerId=0;
  class Element {
    constructor(tag) {
      this.tag=tag; this.children=[]; this.attributes=new Map(); this.dataset={}; this.listeners={};
      const classes=new Set();
      this.classList={add:(...values)=>values.forEach(v=>classes.add(v)),remove:(...values)=>values.forEach(v=>classes.delete(v)),
        contains:value=>classes.has(value),toggle:(value,on)=>{if(on??!classes.has(value)) classes.add(value);else classes.delete(value);}};
      Object.defineProperty(this,'className',{set:value=>{classes.clear();value.split(' ').filter(Boolean).forEach(v=>classes.add(v));}});
      this.style={setProperty(){},removeProperty(){}};
    }
    set id(value) {this.setAttribute('id',value);}
    get id() {return this.getAttribute('id');}
    set src(value) {this.setAttribute('src',value);images.push({image:this,url:value});}
    get src() {return this.getAttribute('src');}
    setAttribute(name,value) {this.attributes.set(name,String(value));}
    getAttribute(name) {return this.attributes.get(name)??null;}
    removeAttribute(name) {this.attributes.delete(name);}
    append(...nodes) {for(const node of nodes) {node.parentElement=this;this.children.push(node);}}
    replaceChild(next,previous) {this.children[this.children.indexOf(previous)]=next;next.parentElement=this;previous.parentElement=null;}
    remove() {if(this.parentElement) this.parentElement.children=this.parentElement.children.filter(node=>node!==this);this.parentElement=null;}
    querySelector(selector) {return this.children.flatMap(node=>[node,...node.descendants()]).find(node=>selector==='img'?node.tag==='img':node.id===selector.slice(1))||null;}
    descendants() {return this.children.flatMap(node=>[node,...node.descendants()]);}
    getBoundingClientRect() {return {};}
    addEventListener(name,callback) {this.listeners[name]=callback;}
  }
  const player=new Element('main'), deck=new Element('div'), cover=new Element('button'), image=new Element('img');
  deck.id='coverFan'; cover.id='cover'; image.id='artwork'; cover.append(image); deck.append(cover); player.append(deck); player.id='player';
  const context=vm.createContext({console,AbortController,Date,
    document:{createElement:tag=>new Element(tag),querySelector:selector=>player.id===selector.slice(1)?player:player.querySelector(selector)},window:{},
    api:(url,body,signal)=>new Promise((resolve,reject)=>requests.push({url,signal,resolve,reject})),
    navigate:direction=>navigation.push(direction),togglePlayback(){},
    setTimeout:(callback,delay)=>{const id=++timerId;timers.set(id,{callback,delay});return id;},clearTimeout:id=>timers.delete(id)});
  vm.runInContext(fs.readFileSync(path.join(__dirname,'../src/Bridge/wwwroot/fan.js'),'utf8'),context);
  return {fan:context.window.coverFan,images,requests,navigation,player,
    card:id=>deck.descendants().find(node=>node.dataset.trackId===id),
    loaded:id=>{const node=deck.descendants().find(node=>node.dataset.trackId===id);node.querySelector('img').onload();}};
}
const track=id=>({id:'youtube:'+id.repeat(11),provider:'youtube',title:id,artist:'Artist'});
const [a,b,c,p,d]=['A','B','C','P','D'].map(track);
const status=(current,version=1)=>({revision:version,track:current,navigation:{version,count:1,target:current}});
const selection=(id,direction=null,exact=true)=>({requestId:id,direction,exact});
const flush=async()=>{for(let i=0;i<8;i++) await Promise.resolve();};

(async()=>{
  const app=environment();
  app.fan.sync(a,status(a),selection('a'),true);
  assert.equal(app.images.length,0,'Selection preparation started artwork');
  assert.equal(app.requests.length,0,'Selection preparation started recommendations');
  app.fan.sync(a,status(a),null,true);
  assert.equal(app.requests.length,0,'Command completion started recommendations before playing');
  app.fan.sync(a,status(a),null,false);
  assert.deepEqual(app.images.map(request=>request.url),['/api/artwork/AAAAAAAAAAA']);
  assert.equal(app.requests.length,1,'Playing did not release recommendations');
  app.requests[0].resolve({track:a,previous:[p],next:[b,c]}); await flush();
  assert.equal(app.fan.target('next').id,b.id);
  assert.equal(app.fan.target('previous').id,p.id);
  app.loaded(a.id); app.loaded(b.id); app.loaded(p.id);
  const staleImage=app.card(c.id).querySelector('img'), staleLoad=staleImage.onload;
  const loadedCover=app.card(b.id).querySelector('img').src, before=app.images.length;
  app.fan.sync(a,status(a,2),null,true);
  assert.equal(app.requests.length,1,'Buffering started a new recommendations request');
  assert.equal(app.images.length,before,'Buffering started new images');
  assert.equal(app.card(b.id).querySelector('img').src,loadedCover,'Buffering discarded an available neighbor cover');
  assert.equal(staleImage.src,null,'Buffering did not cancel an unfinished image');
  staleLoad();
  assert.equal(staleImage.parentElement.classList.contains('has-art'),false,'Cancelled image completion marked stale artwork ready');
  assert.equal(app.fan.canPrevious(),true);
  app.card(b.id).children[0].onclick();
  assert.deepEqual(app.navigation,['next'],'Buffering blocked explicit neighbor navigation');
  app.fan.sync(b,status(b,3),selection('b','next'),true);
  assert.equal(app.fan.target('next').id,c.id,'Known next metadata was dropped while loading');
  assert.equal(app.fan.target('previous').id,a.id);
  assert.equal(app.images.length,before,'Manual selection started optional images before playing');
  app.fan.sync(b,status(b,3),selection('unknown','next',false),true);
  assert.equal(app.fan.target('next').id,c.id,'Unknown rapid selection discarded cached neighbors');
  assert.equal(app.fan.target('previous').id,a.id);
  app.fan.sync(b,status(b,3),null,false);
  assert.equal(app.requests.length,2);
  assert.ok(app.images.slice(before).some(request=>request.url==='/api/artwork/BBBBBBBBBBB'),'Playing did not upgrade the current cover');
  const oldPreview=app.requests[1];
  app.fan.sync(c,status(c,4),selection('c','next'),true);
  assert.equal(oldPreview.signal.aborted,true,'New selection did not cancel stale recommendations');
  const afterSelection=app.images.length;
  oldPreview.resolve({track:b,previous:[a],next:[d]}); await flush();
  assert.equal(app.images.length,afterSelection,'Stale recommendations started new artwork');
  assert.equal(app.fan.target('next'),null,'Stale recommendations replaced the latest selection');
  assert.equal(app.fan.target('previous').id,b.id);
  app.fan.sync(c,status(c,4),null,false);
  assert.equal(app.requests.length,3,'Playback after rapid selection did not release recommendations');
  assert.ok(app.images.slice(afterSelection).some(request=>request.url==='/api/artwork/CCCCCCCCCCC'));
  const rebufferPreview=app.requests[2];
  app.fan.sync(c,status(c,4),null,true);
  assert.equal(rebufferPreview.signal.aborted,true,'Buffering did not cancel an outstanding preview');
  rebufferPreview.resolve({track:c,previous:[b],next:[d]}); await flush();
  assert.equal(app.fan.target('next'),null,'Preview completion escaped buffering cancellation');
  console.log('PASS: audible-playback priority, cached neighbors/manual navigation, rapid selection and stale work cancellation');
  const compatible=environment(); compatible.fan.sync(a,status(a),null);
  assert.equal(compatible.requests.length,1,'Optional foreground flag broke existing callers');
  assert.equal(compatible.images.length,1);
  console.log('PASS: omitted foreground flag retains existing fan API behavior');
})().catch(error=>{console.error(error);process.exitCode=1;});
