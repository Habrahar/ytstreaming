(() => {
  let library=null, liked=new Set(), entries=new Map(), selected='liked', current=null, pickerTrack=null;
  let loading=null, saving=false, toastTimer;
  const dialog=$('#libraryDialog'), picker=$('#playlistPicker');
  const covers=new TrackArtLoader($('#libraryTracks'));
  const songCount=n=>n+' '+(n%100>=11&&n%100<=14?'песен':({1:'песня',2:'песни',3:'песни',4:'песни'}[n%10]||'песен'));

  function feedback(text='') {
    $('#libraryFeedback').textContent=text; $('#pickerFeedback').textContent=text;
    $('#libraryNotice').textContent=text;
    clearTimeout(toastTimer); toastTimer=setTimeout(()=>$('#libraryNotice').textContent='',4000);
  }
  function accept(value) {
    if(library && value.revision<=library.revision) return;
    library=value; liked=new Set(value.likedIds); entries=new Map(value.tracks.map(entry=>[entry.track.id,entry]));
    renderCollections(); renderPicker(); updateButtons();
  }
  async function load() {
    if(loading) return loading;
    loading=api('/api/library').then(accept).catch(error=>feedback(error.message)).finally(()=>loading=null);
    return loading;
  }
  async function change(action) {
    if(saving) return false;
    saving=true; updateButtons();
    try { await action(); return true; }
    catch(error) { feedback(error.message); return false; }
    finally { saving=false; updateButtons(); }
  }
  function updateButtons() {
    for(const button of document.querySelectorAll('.like-track')) {
      const active=liked.has(button.dataset.id);
      button.classList.toggle('liked',active); button.textContent=active?'♥':'♡';
      button.setAttribute('aria-pressed',String(active));
      button.setAttribute('aria-label',(active?'Убрать лайк':'Поставить лайк')+(button.dataset.title?' — '+button.dataset.title:''));
      button.title=active?'Убрать из понравившихся':'В понравившиеся';
      button.disabled=!library || saving || !button.dataset.id;
    }
    for(const button of document.querySelectorAll('.add-track')) button.disabled=!library || saving || !button.dataset.id;
    $('#addCurrent').disabled=!library || saving || current?.provider!=='youtube';
    for(const button of document.querySelectorAll('.playlist-choice,.create-form button')) button.disabled=!library || saving;
  }
  function renderCurrent(track) {
    current=track?.provider==='youtube'?track:null;
    $('#likeCurrent').dataset.id=current?.id||'';
    updateButtons();
  }
  function like(track) {
    if(!library) return;
    const active=liked.has(track.id);
    change(async()=>{
      accept(await api('/api/library/likes',{id:track.id,liked:!active}));
      feedback(active?'Убрано из понравившихся':'Добавлено в понравившиеся');
    });
  }
  function decorate(row,track) {
    const heart=document.createElement('button'); heart.type='button'; heart.className='icon-button like-track';
    heart.dataset.id=track.id; heart.dataset.title=track.title;
    const active=liked.has(track.id); heart.textContent=active?'♥':'♡'; heart.classList.toggle('liked',active);
    heart.setAttribute('aria-pressed',String(active));
    heart.setAttribute('aria-label',(active?'Убрать лайк — ':'Поставить лайк — ')+track.title);
    heart.title=active?'Убрать из понравившихся':'В понравившиеся'; heart.disabled=!library || saving;
    heart.onclick=()=>like(track);
    const add=document.createElement('button'); add.type='button'; add.className='icon-button add-track';
    add.dataset.id=track.id; add.textContent='+'; add.disabled=!library || saving;
    add.setAttribute('aria-label','Добавить в плейлист — '+track.title); add.title='Добавить в плейлист'; add.onclick=()=>openPicker(track);
    row.append(heart,add);
  }
  function renderCollections() {
    if(!dialog.open) return;
    const tabs=$('#libraryTabs'), list=$('#libraryTracks'), scroll=list.scrollTop;
    covers.stop(true); tabs.replaceChildren(); list.replaceChildren();
    if(!library) {
      empty(list,'Не удалось загрузить библиотеку. Закрой и открой её ещё раз.'); return;
    }
    if(selected!=='liked' && !library.playlists.some(p=>p.id===selected)) selected='liked';
    const collections=[{id:'liked',name:'Понравившиеся',trackIds:library.likedIds},...library.playlists];
    for(const collection of collections) {
      const button=document.createElement('button'); button.className='library-tab'; button.type='button';
      button.setAttribute('aria-pressed',String(selected===collection.id));
      button.setAttribute('aria-label',collection.name+' — '+songCount(collection.trackIds.length));
      button.textContent=collection.name;
      const count=document.createElement('small'); count.textContent=collection.trackIds.length; button.append(count);
      button.onclick=()=>{selected=collection.id; renderCollections();}; tabs.append(button);
    }
    const collection=collections.find(p=>p.id===selected); $('#collectionTitle').textContent=collection.name;
    for(const id of collection.trackIds) {
      const entry=entries.get(id); if(!entry) continue;
      list.append(createTrackRow(entry.track, track=>{dialog.close(); choose(track);}));
    }
    if(!collection.trackIds.length) empty(list,selected==='liked'?'Нажми ♡ у песни — она появится здесь.':'Добавляй песни через + рядом с названием или в плеере.');
    list.scrollTop=scroll; covers.watch();
  }
  function empty(element,text) { const p=document.createElement('p'); p.className='empty-library'; p.textContent=text; element.append(p); }

  function openPicker(track) {
    pickerTrack=track;
    clearTimeout(searchTimer); searchRequest?.abort(); ++searchSequence; stopThumbnails(); covers.stop();
    $('#pickerTrack').textContent=track.title+' — '+track.artist;
    $('#pickerPlaylistName').value=''; feedback(); renderPicker(); picker.showModal();
  }
  function renderPicker() {
    const list=$('#playlistChoices'); list.replaceChildren();
    if(!pickerTrack || !library) return;
    if(!library.playlists.length) empty(list,'Создай первый плейлист ниже.');
    for(const playlist of library.playlists) {
      const active=playlist.trackIds.includes(pickerTrack.id), button=document.createElement('button');
      button.className='playlist-choice'; button.type='button'; button.disabled=saving;
      button.setAttribute('aria-pressed',String(active));
      button.setAttribute('aria-label',(active?'Убрать из плейлиста ':'Добавить в плейлист ')+playlist.name);
      const copy=document.createElement('span'); copy.className='choice-copy'; copy.textContent=playlist.name;
      const count=document.createElement('small'); count.textContent=songCount(playlist.trackIds.length); copy.append(count);
      const check=document.createElement('span'); check.className='check'; check.textContent=active?'✓':'+'; check.setAttribute('aria-hidden','true');
      button.append(copy,check); list.append(button);
      button.onclick=()=>change(async()=>{
        accept(await api('/api/library/playlists/'+encodeURIComponent(playlist.id)+'/tracks',{id:pickerTrack.id,added:!active}));
        feedback(active?'Убрано из «'+playlist.name+'»':'Добавлено в «'+playlist.name+'»');
      });
    }
  }

  window.libraryUI={decorate,renderCurrent,sync:revision=>{if(!library || revision!==library.revision) load();}};
  $('#likeCurrent').onclick=()=>{if(current) like(current);};
  $('#addCurrent').onclick=()=>{if(current) openPicker(current);};
  $('#openLibrary').onclick=async()=>{
    closeSearch(); feedback(); dialog.showModal(); renderCollections(); await load();
  };
  $('#closeLibrary').onclick=()=>dialog.close();
  dialog.addEventListener('close',()=>{covers.stop(true); $('#libraryCreateForm').hidden=true;});
  $('#closePicker').onclick=()=>picker.close();
  picker.addEventListener('close',()=>{pickerTrack=null; if(dialog.open) covers.watch(); if($('#searchShell').classList.contains('open')) watchThumbnails();});
  for(const modal of [dialog,picker]) modal.addEventListener('click',event=>{
    if(event.target!==modal) return;
    const rect=modal.getBoundingClientRect();
    if(event.clientX<rect.left || event.clientX>rect.right || event.clientY<rect.top || event.clientY>rect.bottom) modal.close();
  });
  $('#newPlaylist').onclick=()=>{const form=$('#libraryCreateForm'); form.hidden=!form.hidden; if(!form.hidden) $('#libraryPlaylistName').focus();};
  $('#libraryCreateForm').onsubmit=async event=>{
    event.preventDefault(); const name=$('#libraryPlaylistName').value;
    const ok=await change(async()=>{
      accept(await api('/api/library/playlists',{name}));
      selected=library.playlists.find(p=>p.name.toLowerCase()===name.trim().replace(/\s+/g,' ').toLowerCase())?.id||selected;
      renderCollections(); feedback('Плейлист создан');
    });
    if(ok) {$('#libraryCreateForm').hidden=true; $('#libraryPlaylistName').value='';}
  };
  $('#pickerCreateForm').onsubmit=async event=>{
    event.preventDefault(); if(!pickerTrack) return;
    const track=pickerTrack, name=$('#pickerPlaylistName').value;
    const ok=await change(async()=>{
      accept(await api('/api/library/playlists',{name}));
      const playlist=library.playlists.find(p=>p.name.toLowerCase()===name.trim().replace(/\s+/g,' ').toLowerCase());
      if(!playlist) throw Error('Выбери созданный плейлист в списке.');
      accept(await api('/api/library/playlists/'+playlist.id+'/tracks',{id:track.id,added:true}));
      feedback('Добавлено в «'+playlist.name+'»');
    });
    if(ok) $('#pickerPlaylistName').value='';
  };
  renderCurrent(pending?.track||server?.track); load();
})();
