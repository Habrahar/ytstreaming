// Exercises the real player callbacks without a browser or external playback.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');
const root=path.join(__dirname,'../src/Bridge/wwwroot');
function environment({unsupported=null,noSession=false,rejectLive=false}={}) {
  const elements=new Map(), handlers=new Map(), calls=[], positions=[], requests=[], events={}, timers=new Map(), responses=new Map(), replies=new Map();
  let timerId=0;
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
    TrackArtLoader:class{stop(){}watch(){}},setInterval(){},clearTimeout:id=>timers.delete(id),setTimeout:(callback,delay)=>{const id=++timerId;timers.set(id,{callback,delay});return id;},
    location:{href:'http://127.0.0.1:8090/'},
    fetch:(url,options)=>{requests.push({url,options});if(responses.has(url)) return Promise.resolve({ok:true,json:async()=>responses.get(url)});
      return new Promise(resolve=>{if(options?.body) replies.set(JSON.parse(options.body).requestId,resolve);});}
  });
  vm.runInContext(fs.readFileSync(path.join(root,'media-controls.js'),'utf8'),context);
  vm.runInContext(fs.readFileSync(path.join(root,'player.js'),'utf8'),context);
  context.fixture={ready:true,revision:7,navigation:{canPrevious:true},
    track:{id:'youtube:AAAAAAAAAAA',provider:'youtube',title:'A',artist:'Artist'}};
  return {context,session,handlers,calls,positions,requests,events,audio,timers,responses,elements,
    reply:(id,status)=>replies.get(id)({ok:true,json:async()=>status}),metadataCount:()=>metadataCount};
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
assert.equal(app.requests.filter(r=>r.url==='/api/player/next').length,2,'Repeated Next was blocked during preparation');
let nexts=app.requests.filter(r=>r.url==='/api/player/next');
const first=JSON.parse(nexts[0].options.body), second=JSON.parse(nexts[1].options.body);
assert.equal(first.navigationId,second.navigationId); assert.equal(first.navigationOffset,1); assert.equal(second.navigationOffset,2);
assert.ok(second.commandSequence>first.commandSequence);
assert.equal(nexts[0].options.signal.aborted,false,'Previous command was aborted before the new intent could advance');
assert.equal(app.elements.get('#next').disabled,false,'Next is disabled while preparing');
assert.equal(app.elements.get('#previous').disabled,false,'Previous cannot undo a pending Next');
vm.runInContext('pending=null; server.navigation.canPrevious=false;',app.context);
app.handlers.get('previoustrack')();
assert.equal(app.requests.filter(r=>r.url==='/api/player/previous').length,0,'Remote Previous ran with no history');
vm.runInContext('server.navigation.canPrevious=true;',app.context);
app.handlers.get('previoustrack')();
assert.equal(app.requests.filter(r=>r.url==='/api/player/previous').length,1);
console.log('PASS: native track actions, seeking disabled, idempotent play/pause, cumulative rapid navigation and re-registration');

const partial=environment({unsupported:'previoustrack',rejectLive:true});
assert.equal(typeof partial.handlers.get('nexttrack'),'function','One unsupported action stopped subsequent registration');
assert.ok(partial.requests.some(r=>r.url==='/api/status'),'Unsupported action stopped the player');
assert.equal(partial.positions.at(-1),undefined,'Unsupported live position did not clear position state');
const absent=environment({noSession:true});
assert.ok(absent.requests.some(r=>r.url==='/api/status'),'Missing Media Session stopped the player');
console.log('PASS: partial/missing Media Session support does not stop playback setup');

(async()=>{
  const rapid=environment(); vm.runInContext('server=fixture;',rapid.context);
  // Missing recommendation metadata must not turn an old side cover into the
  // expected ID of a later cumulative offset.
  rapid.handlers.get('nexttrack')();
  rapid.context.window.coverFan={sync(){},canPrevious(){return false;},target(){return rapid.context.fixture.track;},
    expected(){return {expectedId:rapid.context.fixture.track.id,expectedRevision:7};}};
  rapid.handlers.get('nexttrack')();
  let unknown=rapid.requests.filter(r=>r.options?.body).map(r=>JSON.parse(r.options.body));
  assert.equal(unknown[1].navigationOffset,2); assert.equal(unknown[1].expectedId,undefined,'An unknown intermediate target used a stale side cover');
  delete rapid.context.window.coverFan;
  vm.runInContext('pending=null; navigationBurst=null;',rapid.context);
  const start=rapid.requests.length;
  for(let i=0;i<20;i++) rapid.handlers.get('nexttrack')();
  for(let i=0;i<6;i++) rapid.handlers.get('previoustrack')();
  const commands=rapid.requests.slice(start).filter(r=>r.options?.body).map(r=>JSON.parse(r.options.body));
  assert.equal(commands.length,26); assert.equal(commands.at(-1).navigationOffset,14);
  const latest=commands.at(-1), chosen={...rapid.context.fixture,revision:8,requestId:latest.requestId,
    track:{...rapid.context.fixture.track,id:'youtube:OOOOOOOOOOO',title:'Last choice'}};
  rapid.reply(latest.requestId,chosen);
  for(let i=0;i<12;i++) await Promise.resolve();
  rapid.reply(commands[0].requestId,{...rapid.context.fixture,track:{...rapid.context.fixture.track,title:'Stale choice'}});
  for(let i=0;i<12;i++) await Promise.resolve();
  assert.equal(rapid.elements.get('#title').textContent,'Last choice','Late response replaced the last selected song');
  console.log('PASS: 20 Next and 6 Previous before loading, cumulative target and late-response rejection');
  const network=environment(); vm.runInContext('server=fixture;',network.context);
  network.responses.set('/api/status',network.context.fixture);
  network.handlers.get('play')(); network.audio.onplaying(); network.audio.error={code:2};
  for(let i=0;i<3;i++) {
    network.audio.onerror(); assert.equal(network.timers.size,1,'Lost connection did not schedule a retry');
    const [id,timer]=network.timers.entries().next().value; network.timers.delete(id); await timer.callback();
  }
  network.audio.onerror(); assert.equal(network.timers.size,0,'Retries were unbounded');
  assert.equal(network.audio.playCalls,4,'Recovery did not reconnect to the running broadcast');
  network.audio.onplaying(); network.audio.onerror();
  const retry=network.timers.values().next().value; network.handlers.get('pause')();
  assert.equal(network.timers.size,0); await retry.callback(); assert.equal(network.audio.playCalls,4,'Retry revived paused audio');
  network.handlers.get('play')(); network.audio.onplaying();
  network.responses.set('/api/status',{...network.context.fixture,ready:false,state:'stopped'});
  await network.audio.onended(); assert.equal(network.timers.size,0,'Real end-of-song incorrectly reconnected');
  console.log('PASS: transient disconnect recovery, bounded retries, pause cancels recovery and real EOF stays ended');
})().catch(error=>{console.error(error);process.exitCode=1;});
