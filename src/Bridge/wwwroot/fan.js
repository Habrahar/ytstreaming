(() => {
  const deck=document.querySelector('#coverFan'), rail=document.createElement('div'); rail.className='fan-track';
  const initial=document.querySelector('#cover'), wrapper=document.createElement('div');
  wrapper.className='fan-item'; wrapper.dataset.slot='0'; initial.classList.add('fan-face');
  initial.parentElement.replaceChild(rail,initial); wrapper.append(initial); rail.append(wrapper);
  const cards=new Map([['empty',{node:wrapper,face:initial,image:initial.querySelector('img'),track:null,slot:0}]]);
  let currentId=null, view=[null,null,null,null,null], preview=null, previewKey='', requestKey='', request=null, retryAt=0;
  let state=null, busy=false, gesture=null, suppressClickUntil=0, selectionId=null, foreground=false;
  const q=selector=>document.querySelector(selector);
  function cancelPreview() {request?.abort(); request=null; requestKey='';}
  function cancelImages() {
    for(const card of cards.values()) if(card.image.getAttribute('src') && card.loadedUrl!==card.image.getAttribute('src')) {
      ++card.imageVersion; card.image.onload=null; card.image.onerror=null; card.image.removeAttribute('src');
      card.face.classList.remove('has-art');
    }
  }
  function loadImage(card,url) {
    if(card.image.getAttribute('src')===url) return;
    const version=card.imageVersion=(card.imageVersion||0)+1;
    card.image.onload=()=>{
      if(version!==card.imageVersion || card.image.getAttribute('src')!==url) return;
      card.loadedUrl=url; card.face.classList.add('has-art');
    };
    card.image.onerror=()=>{if(version===card.imageVersion) card.face.classList.remove('has-art');};
    card.image.src=url;
  }
  function makeCard(track,key) {
    const node=document.createElement('div'); node.className='fan-item entering';
    node.dataset.trackId=track?.id||'';
    const face=document.createElement('button'); face.type='button'; face.className='cover fan-face';
    const record=document.createElement('div'); record.className='record'; record.setAttribute('aria-hidden','true');
    const image=document.createElement('img'); image.className='fan-art'; image.alt=''; image.decoding='async'; image.draggable=false;
    image.referrerPolicy='no-referrer';
    const caption=document.createElement('span'); caption.className='fan-caption'; caption.setAttribute('aria-hidden','true');
    const title=document.createElement('strong'), artist=document.createElement('small');
    title.textContent=track?.title||''; artist.textContent=track?.artist||''; caption.append(title,artist);
    face.append(record,image); node.append(face,caption); rail.append(node);
    const card={node,face,image,track,slot:0,caption,title,artist}; cards.set(key,card);
    face.onclick=()=>{
      if(Date.now()<suppressClickUntil) return;
      if(card.slot===0 && !busy) togglePlayback();
      else if(Math.abs(card.slot)===1 && card.track) navigate(card.slot<0?'previous':'next');
    };
    return card;
  }
  function layout(track,tracks,preparing) {
    const visible=new Set();
    for(const card of cards.values()) {card.face.removeAttribute('id'); card.image.removeAttribute('id');}
    tracks.forEach((item,index)=>{
      const slot=index-2;
      if(!item && Math.abs(slot)===2) return;
      const key=item?.id||(slot===0?'empty':'empty-'+slot); visible.add(key);
      const card=cards.get(key)||makeCard(item,key); card.track=item; card.slot=slot; clearTimeout(card.removeTimer); card.removeTimer=null;
      card.node.dataset.slot=slot; card.node.classList.toggle('empty',!item && slot!==0);
      card.node.setAttribute('aria-hidden',String(Math.abs(slot)>1 || !item));
      if(slot===0) {card.face.id='cover'; card.image.id='artwork';}
      if(card.caption && !item) {
        card.title.textContent=slot<0?'История пуста':requestKey?'Подбираем музыку…':'Следующая песня'; card.artist.textContent='';
      }
      card.face.disabled=(busy && slot===0) || !item || Math.abs(slot)>1; card.face.tabIndex=card.face.disabled?-1:0;
      card.face.setAttribute('aria-label',item?(slot<0?'Предыдущая песня: ':slot>0?'Следующая песня: ':'Воспроизвести или приостановить: ')+item.title:'');
      if(item && !foreground) {
        const url='/api/artwork/'+encodeURIComponent(item.id.slice(8))+(slot===0?'':'?size=card');
        card.image.fetchPriority=slot===0?'high':'low';
        loadImage(card,url);
      }
      if(card.node.classList.contains('entering')) {card.node.getBoundingClientRect(); card.node.classList.remove('entering');}
    });
    for(const [key,card] of cards) if(!visible.has(key)) {
      card.node.dataset.slot=card.slot<0?'-3':'3'; card.face.disabled=true; card.node.setAttribute('aria-hidden','true');
      if(!card.removeTimer) card.removeTimer=setTimeout(()=>{
        card.image.removeAttribute('src'); card.node.remove(); cards.delete(key);
      },650);
    }
    q('#player').classList.toggle('has-neighbors',!!tracks[1]||!!tracks[3]);
  }
  function fromPreview(track,value) { return [value.previous[1]||null,value.previous[0]||null,track,value.next[0]||null,value.next[1]||null]; }
  async function loadPreview(key,status,track) {
    request?.abort(); const own=request=new AbortController(); requestKey=key;
    try {
      const value=await api('/api/player/neighbors?revision='+status.revision+'&version='+status.navigation.version,undefined,own.signal);
      if(own!==request || foreground || currentId!==track.id || value.track?.id!==track.id) return;
      preview=value; previewKey=key; retryAt=0; view=fromPreview(track,value); layout(track,view,false);
    } catch(error) {
      if(error.name!=='AbortError' && own===request) retryAt=error.status===409?0:Date.now()+10000;
    } finally {if(own===request) {requestKey=''; request=null;}}
  }
  function sync(track,status,selection,foregroundPlayback=false) {
    foreground=foregroundPlayback;
    state=status; busy=!!selection || !!status?.navigation?.busy;
    if(selectionId!==(selection?.requestId||null)) {
      selectionId=selection?.requestId||null; cancelPreview(); cancelImages();
    }
    if(foreground) {cancelPreview(); cancelImages();}
    if(track?.provider!=='youtube') track=null;
    if(selection && !selection.exact) {
      // Retain known neighbors while a rapid manual jump has no exact target.
      currentId=track?.id; view[2]=track;
      layout(track,view,true); return;
    }
    if(currentId!==track?.id) {
      const ordered=preview?[...preview.previous].reverse().concat(view[2]?[view[2]]:[],preview.next):view;
      const centre=preview?preview.previous.length:2;
      let position=track?ordered.findIndex((t,i)=>t?.id===track.id && (selection?.direction==='next'?i>centre:selection?.direction==='previous'?i<centre:true)):-1;
      if(selection && !selection.direction) position=-1;
      if(selection?.direction==='previous') for(let i=centre-1;i>=0;i--) if(ordered[i]?.id===track?.id) {position=i;break;}
      if(position>=0) {
        preview={previous:ordered.slice(0,position).reverse(),next:ordered.slice(position+1),track};
        view=fromPreview(track,preview);
      } else {
        const actual=status?.track?.provider==='youtube' && status.navigation?.count>0?status.track:null;
        view=[null,selection && !selection.direction && actual?.id!==track?.id?actual:null,track,null,null]; preview=null;
      }
      view[2]=track; currentId=track?.id; previewKey=''; retryAt=0;
      cancelPreview(); cancelImages();
    }
    layout(track,view,busy);
    const target=status?.navigation?.target||status?.track;
    if(foreground || !track || !status || target?.id!==track.id || (selection && status.navigation?.requestId!==selection.requestId)) return;
    const key=status.revision+':'+status.navigation.version;
    if(key!==previewKey && key!==requestKey && Date.now()>=retryAt) loadPreview(key,status,track);
  }
  window.coverFan={sync,target:direction=>view[direction==='previous'?1:3],
    canPrevious:()=>preview?preview.previous.length>0:view[1]?true:undefined,
    expected:direction=>({expectedId:view[direction==='previous'?1:3]?.id,expectedRevision:state?.revision})};
  deck.addEventListener('pointerdown',event=>{
    if(event.button!==0 || !event.isPrimary || !currentId) return;
    gesture={id:event.pointerId,x:event.clientX,y:event.clientY,active:false};
  });
  deck.addEventListener('pointermove',event=>{
    if(!gesture || event.pointerId!==gesture.id) return;
    const dx=event.clientX-gesture.x, dy=event.clientY-gesture.y;
    if(!gesture.active && Math.abs(dy)>12 && Math.abs(dy)>Math.abs(dx)) {gesture=null; return;}
    if(!gesture.active && Math.abs(dx)>8 && Math.abs(dx)>Math.abs(dy)*1.3) {
      gesture.active=true; deck.setPointerCapture(event.pointerId); deck.classList.add('dragging');
    }
    if(gesture.active) {deck.style.setProperty('--drag',Math.max(-110,Math.min(110,dx*.65))+'px'); deck.style.setProperty('--lean',Math.max(-3,Math.min(3,dx/40))+'deg');}
  });
  function endGesture(event,cancelled=false) {
    if(!gesture || event.pointerId!==gesture.id) return;
    const dx=event.clientX-gesture.x, active=gesture.active; gesture=null;
    deck.classList.remove('dragging'); deck.style.removeProperty('--drag'); deck.style.removeProperty('--lean');
    if(deck.hasPointerCapture(event.pointerId)) deck.releasePointerCapture(event.pointerId);
    if(!active) return;
    suppressClickUntil=Date.now()+400;
    if(!cancelled && Math.abs(dx)>44) {
      const direction=dx<0?'next':'previous';
      navigate(direction);
    }
  }
  deck.addEventListener('pointerup',event=>endGesture(event));
  deck.addEventListener('pointercancel',event=>endGesture(event,true));
  deck.addEventListener('keydown',event=>{
    if(['ArrowLeft','ArrowRight'].includes(event.key)) {
      event.preventDefault(); navigate(event.key==='ArrowRight'?'next':'previous');
    }
  });
})();
