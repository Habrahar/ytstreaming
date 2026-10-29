// Exercises the real player callbacks without a browser or external playback.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');
const root=path.join(__dirname,'../src/Bridge/wwwroot');
function environment({unsupported=null,noSession=false,rejectLive=false}={}) {
  const elements=new Map(), handlers=new Map(), calls=[], positions=[], requests=[], events={};
  let metadataCount=0;
  function element(selector) {
    if(!elements.has(selector)) elements.set(selector,{value:'',classList:{add(){},remove(){},toggle(){}},dataset:{},
      setAttribute(){},removeAttribute(){},replaceChildren(){},blur(){},focus(){}});
    return elements.get(selector);
  }
  const audio=element('#audio'); audio.paused=true; audio.playCalls=0;
  audio.pause=()=>{audio.paused=true;}; audio.load=()=>{};
  audio.play=()=>{audio.playCalls++;audio.paused=false;return Promise.resolve();};
  audio.addEventListener=(event,handler)=>events[event]=handler;
  const session={metadata:null,playbackState:'none',setActionHandler(action,handler){
    calls.push(action); if(action===unsupported) throw Error('Unsupported action'); handlers.set(action,handler);
  },setPositionState(value){positions.push(value);if(rejectLive&&value) throw Error('Unsupported infinite duration');}};
  class Metadata { constructor(value){metadataCount++;Object.assign(this,value);} }
  const context=vm.createContext({console,URL,AbortController,
    document:{querySelector:element,addEventListener(){}},
    navigator:noSession?{}:{mediaSession:session},
    window:{MediaMetadata:Metadata,addEventListener:(event,handler)=>events[event]=handler},
    TrackArtLoader:class{stop(){}watch(){}},setInterval(){},clearTimeout(){},setTimeout(){},
    location:{href:'http://127.0.0.1:8090/'},
    fetch:(url,options)=>{requests.push({url,options});return new Promise(()=>{});}
  });
  vm.runInContext(fs.readFileSync(path.join(root,'media-controls.js'),'utf8'),context);
  vm.runInContext(fs.readFileSync(path.join(root,'player.js'),'utf8'),context);
  context.fixture={ready:true,revision:7,navigation:{canPrevious:true},
    track:{id:'youtube:AAAAAAAAAAA',provider:'youtube',title:'A',artist:'Artist'}};
  return {context,session,handlers,calls,positions,requests,events,audio,metadataCount:()=>metadataCount};
}

const app=environment();
for(const action of ['seekbackward','seekforward','seekto']) assert.equal(app.handlers.get(action),null);
for(const action of ['play','pause','previoustrack','nexttrack']) assert.equal(typeof app.handlers.get(action),'function');
assert.equal(app.positions.at(-1).duration,Infinity);
vm.runInContext('server=fixture; render(); render();',app.context);
const metadata=app.session.metadata;
assert.equal(app.metadataCount(),1,'Polling recreates native metadata');
app.handlers.get('play')(); app.handlers.get('play')();
assert.equal(app.audio.playCalls,1,'Repeated remote Play toggled/reconnected playback');
assert.equal(app.session.playbackState,'playing','Pending audio preparation declared paused');
app.handlers.get('pause')(); app.handlers.get('pause')();
assert.equal(app.audio.paused,true); assert.equal(app.session.playbackState,'paused');
app.handlers.get('play')(); assert.equal(app.audio.playCalls,2);
const callsBefore=app.calls.length; app.audio.onplaying(); app.events.loadedmetadata(); app.events.pageshow();
assert.equal(app.calls.length,callsBefore+21,'Native playback/metadata/pageshow did not reassert commands');
assert.equal(app.session.metadata,metadata,'Native event rebuilt unchanged metadata');
app.handlers.get('nexttrack')();
assert.equal(app.requests.filter(r=>r.url==='/api/player/next').length,1);
app.handlers.get('nexttrack')();
assert.equal(app.requests.filter(r=>r.url==='/api/player/next').length,1,'Duplicate Next bypassed pending selection guard');
vm.runInContext('pending=null; server.navigation.canPrevious=false;',app.context);
app.handlers.get('previoustrack')();
assert.equal(app.requests.filter(r=>r.url==='/api/player/previous').length,0,'Remote Previous ran with no history');
vm.runInContext('server.navigation.canPrevious=true;',app.context);
app.handlers.get('previoustrack')();
assert.equal(app.requests.filter(r=>r.url==='/api/player/previous').length,1);
console.log('PASS: native track actions, seeking disabled, idempotent play/pause, pending navigation guard and re-registration');

const partial=environment({unsupported:'previoustrack',rejectLive:true});
assert.equal(typeof partial.handlers.get('nexttrack'),'function','One unsupported action stopped subsequent registration');
assert.ok(partial.requests.some(r=>r.url==='/api/status'),'Unsupported action stopped the player');
assert.equal(partial.positions.at(-1),undefined,'Unsupported live position did not clear position state');
const absent=environment({noSession:true});
assert.ok(absent.requests.some(r=>r.url==='/api/status'),'Missing Media Session stopped the player');
console.log('PASS: partial/missing Media Session support does not stop playback setup');
