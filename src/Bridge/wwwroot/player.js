const $ = selector => document.querySelector(selector);
const audio = $('#audio');
let server = null, pending = null, selectionSequence = 0, attachedRevision = -1;
let wantsAudio = false, phase = 'idle', controlRequest = null, refreshing = false, connectionSequence = 0;
let searchRequest = null, searchSequence = 0, searchTimer, intentionalReset = false;
let completedSearch = '';
let artworkTrack = null;
const searchArt = new TrackArtLoader($('#results'));
function stopThumbnails(release=false) { searchArt.stop(release); }
function watchThumbnails() { searchArt.watch(); }

async function api(path, body, signal) {
  const response = await fetch(path, {signal, cache:'no-store', ...(body === undefined ? {} : {
    method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(body)
  })});
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || 'Не удалось связаться с плеером.');
  return data;
}

function notice(text = '') { $('#message').textContent = text; }
function render() {
  const track = pending?.track || server?.track;
  const isMusic = track?.provider === 'youtube';
  $('#title').textContent = isMusic ? track.title : 'Найди свою музыку';
  $('#artist').textContent = isMusic ? track.artist : 'Начни с поиска наверху';
  $('#duration').textContent = isMusic ? track.duration || '' : '';
  const artworkUrl = isMusic ? '/api/artwork/' + encodeURIComponent(track.id.slice(8)) : null;
  if (artworkTrack !== track?.id) {
    artworkTrack = track?.id;
    $('#cover').classList.remove('has-art');
    if (artworkUrl) { $('#artwork').src=artworkUrl; $('#artwork').alt='Обложка: '+track.title; }
    else $('#artwork').removeAttribute('src');
  }
  const status = pending ? (pending.direction ? 'Готовим '+(pending.direction==='next'?'следующую':'предыдущую')+' песню…' : 'Готовим выбранную песню…') : ({idle:'Выбери песню', buffering:'Подключаем звук…',
    playing:'Сейчас играет', paused:'На паузе', ended:'Трек закончился', gesture:'Нажми ▶, чтобы включить звук',
    error:'Не удалось воспроизвести'})[phase];
  $('#status').textContent = status;
  $('#player').dataset.phase = pending ? 'preparing' : phase;
  $('#play').disabled = !!pending || !isMusic;
  $('#previous').disabled = !!pending || !server?.navigation?.canPrevious;
  $('#next').disabled = !!pending || !isMusic;
  const playing = wantsAudio && !audio.paused && !['error','gesture','ended'].includes(phase);
  $('#play').setAttribute('aria-label', playing ? 'Пауза' : 'Воспроизвести');
  $('#playIcon').textContent = playing ? 'Ⅱ' : '▶';
  $('#cover').classList.toggle('spinning', phase === 'playing' && !pending);
  if ('mediaSession' in navigator && isMusic) {
    navigator.mediaSession.metadata = new MediaMetadata({title:track.title, artist:track.artist,
      artwork:artworkUrl?[{src:new URL(artworkUrl,location.href).href}]:[]});
    navigator.mediaSession.playbackState = phase === 'playing' ? 'playing' : 'paused';
  }
  window.libraryUI?.renderCurrent(track);
}

function disconnect() {
  ++connectionSequence;
  intentionalReset = true;
  audio.pause(); audio.removeAttribute('src'); audio.load();
  intentionalReset = false;
}

// Called synchronously from a click: no metadata/network await before play().
// The server holds this exact selection's connection until audio is ready.
function connect(query) {
  disconnect();
  const connection = connectionSequence;
  phase = 'buffering';
  audio.src = '/stream.mp3?' + query + '&t=' + Date.now();
  const promise = audio.play();
  render();
  return Promise.resolve(promise).then(() => true).catch(error => {
    if (connection !== connectionSequence) return false;
    if (error.name === 'AbortError') return false;
    phase = error.name === 'NotAllowedError' ? 'gesture' : 'error';
    notice(error.name === 'NotAllowedError' ? 'Браузер ждёт нажатия ▶. Песня уже подготовлена.' : 'Не удалось подключить звук. Нажми ▶ ещё раз.');
    render();
    return false;
  });
}

function startSelection(track, path, body, direction = null) {
  const sequence = ++selectionSequence;
  controlRequest?.abort();
  controlRequest = new AbortController();
  const requestId = Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
  pending = {track, requestId, direction};
  wantsAudio = true;
  attachedRevision = -1;
  notice(); closeSearch(); render();
  const command = api(path, {...body, requestId}, controlRequest.signal);
  const playing = connect('requestId=' + encodeURIComponent(requestId));
  Promise.all([command, playing]).then(([status]) => {
    if (sequence !== selectionSequence) return;
    server = status; attachedRevision = status.revision; pending = null;
    render();
  }).catch(error => {
    if (sequence !== selectionSequence) return;
    pending = null; wantsAudio = false; disconnect();
    phase = 'error'; notice(error.message); render();
  });
}

function choose(track) { startSelection(track, '/api/player/play', {id:track.id}); }
function navigate(direction) {
  if (!server?.track || pending) return;
  startSelection(server.track, '/api/player/'+direction, {}, direction);
}

function togglePlayback() {
  notice();
  if (wantsAudio && !audio.paused) {
    wantsAudio = false; audio.pause(); phase = 'paused'; render(); return;
  }
  if (!server?.track || server.track.provider !== 'youtube') return;
  wantsAudio = true;
  if (server.ready) {
    attachedRevision = server.revision;
    connect('revision=' + server.revision);
  } else choose(server.track);
}

async function refresh() {
  if (refreshing) return;
  refreshing = true;
  const sequence = selectionSequence;
  try {
    const status = await api('/api/status');
    if (sequence !== selectionSequence || (server && status.revision < server.revision)) return;
    server = status;
    window.libraryUI?.sync(status.libraryRevision);
    if (pending && status.latestRequestId === pending.requestId && status.pending)
      pending.track = status.pending;
    if (!pending && wantsAudio && server.ready && attachedRevision !== server.revision) {
      attachedRevision = server.revision;
      connect('revision=' + server.revision);
    }
    if (!pending && !server.ready && server.state === 'error') {
      phase = 'error'; notice('Источник прервал звук. Нажми ▶ для повторного запуска.');
    }
    render();
  } catch { notice('Нет связи с Raspberry Pi. Проверь домашний Wi-Fi.'); }
  finally { refreshing = false; }
}

function openSearch() { $('#searchShell').classList.add('open'); $('#backdrop').hidden = false; $('#search').setAttribute('aria-expanded','true'); watchThumbnails(); }
function closeSearch() { $('#searchShell').classList.remove('open'); $('#backdrop').hidden = true; $('#search').setAttribute('aria-expanded','false'); $('#search').blur(); clearTimeout(searchTimer); searchRequest?.abort(); ++searchSequence; stopThumbnails(); }
async function search() {
  const sequence = ++searchSequence, query = $('#search').value.trim();
  searchRequest?.abort(); searchRequest = new AbortController();
  stopThumbnails(true);
  completedSearch='';
  const results = $('#results'); results.replaceChildren();
  $('#clear').hidden = !query;
  if (query.length < 2) { $('#searchHint').textContent = 'Название песни или исполнитель'; return; }
  $('#searchHint').textContent = 'Ищем музыку…';
  try {
    const tracks = await api('/api/youtube/search?q=' + encodeURIComponent(query), undefined, searchRequest.signal);
    if (sequence !== searchSequence) return;
    completedSearch=query;
    $('#searchHint').textContent = tracks.length ? 'Нажми на песню, чтобы включить' : 'Ничего не найдено. Попробуй другой запрос.';
    for (const track of tracks) results.append(createTrackRow(track));
    watchThumbnails();
  } catch(error) { if (sequence === searchSequence && error.name !== 'AbortError') $('#searchHint').textContent=error.message; }
}

function createTrackRow(track, select = choose) {
  const row=document.createElement('div'); row.className='track-row'; row.setAttribute('role','listitem');
  const play=document.createElement('button'); play.type='button'; play.className='result';
  play.setAttribute('aria-label','Включить '+track.title+' — '+track.artist);
  const icon=document.createElement('span'); icon.className='result-icon'; icon.textContent='♫';
  const image=document.createElement('img'); image.className='track-art'; image.alt='';
  image.decoding='async'; image.dataset.videoId=track.id.slice(8); icon.append(image);
  const copy=document.createElement('span'); copy.className='result-copy';
  const title=document.createElement('strong'); title.textContent=track.title;
  const artist=document.createElement('small'); artist.textContent=track.artist;
  const duration=document.createElement('span'); duration.className='result-duration'; duration.textContent=track.duration||'';
  copy.append(title,artist); play.append(icon,copy,duration); play.onclick=()=>select(track); row.append(play);
  window.libraryUI?.decorate(row,track);
  return row;
}

$('#search').onfocus = () => {
  openSearch();
  if ($('#search').value.trim().length >= 2 && $('#search').value.trim() !== completedSearch) {
    clearTimeout(searchTimer); searchTimer=setTimeout(search,0);
  }
};
$('#search').oninput = () => { openSearch(); ++searchSequence; searchRequest?.abort(); clearTimeout(searchTimer); searchTimer=setTimeout(search,300); };
$('#clear').onclick = () => { $('#search').value=''; search(); $('#search').focus(); };
$('#backdrop').onclick = closeSearch;
document.addEventListener('keydown', event => {
  if (event.key === 'Escape') closeSearch();
  if (event.target === $('#search') && ['Enter','ArrowDown'].includes(event.key)) {
    const first=$('#results .result'); if(first) { event.preventDefault(); event.key==='Enter'?first.click():first.focus(); }
  }
});
$('#play').onclick=togglePlayback;
$('#previous').onclick=()=>navigate('previous');
$('#next').onclick=()=>navigate('next');
$('#artwork').onload=()=>$('#cover').classList.add('has-art');
$('#artwork').onerror=()=>$('#cover').classList.remove('has-art');
$('#stop').onclick=async()=>{
  ++selectionSequence; controlRequest?.abort(); pending=null; wantsAudio=false; disconnect(); phase='idle'; render();
  try { server=await api('/api/player/stop',{}); render(); } catch(error) { notice(error.message); }
};
audio.onplaying=()=>{ phase='playing'; notice(); render(); };
audio.onwaiting=()=>{ if(wantsAudio) { phase='buffering'; render(); } };
audio.onpause=()=>{ if(!intentionalReset && !pending && !wantsAudio) { phase='paused'; render(); } };
audio.onended=async()=>{
  if(pending || !wantsAudio) return;
  const sequence=selectionSequence;
  phase='buffering'; render();
  try {
    const status=await api('/api/status');
    if(sequence!==selectionSequence || pending || !wantsAudio) return;
    server=status;
    if(status.ready && attachedRevision!==status.revision) {
      attachedRevision=status.revision; connect('revision='+status.revision); return;
    }
    // A different phone may be changing the common stream. Follow its new
    // revision when ready instead of treating the closed old response as EOF.
    if(status.pending || status.state==='starting') return;
    phase='ended'; wantsAudio=false; render();
  } catch { phase='error'; notice('Связь прервалась. Нажми ▶ для подключения.'); render(); }
};
audio.onerror=()=>{ if(!intentionalReset && !pending && wantsAudio) { phase='error'; notice('Звук прервался. Нажми ▶ для подключения.'); render(); } };
if ('mediaSession' in navigator) {
  navigator.mediaSession.setActionHandler('play',togglePlayback);
  navigator.mediaSession.setActionHandler('pause',()=>{ wantsAudio=false; audio.pause(); phase='paused'; render(); });
  navigator.mediaSession.setActionHandler('previoustrack',()=>navigate('previous'));
  navigator.mediaSession.setActionHandler('nexttrack',()=>navigate('next'));
}
search(); refresh(); setInterval(refresh,1500);
