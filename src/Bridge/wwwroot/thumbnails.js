// Each scrollable list has its own small, cancellable image queue.
class TrackArtLoader {
  constructor(root) {
    this.root=root; this.generation=0; this.active=0; this.queue=[];
    this.urls=new Set(); this.abort=new AbortController(); this.open=false;
    this.observer='IntersectionObserver' in window ? new IntersectionObserver(entries => {
      for (const entry of entries) if(entry.isIntersecting) this.enqueue(entry.target);
    }, {root,rootMargin:'60px'}) : null;
  }
  stop(release=false) {
    this.open=false; ++this.generation; this.abort.abort(); this.abort=new AbortController();
    this.queue=[]; this.active=0; this.observer?.disconnect();
    for(const image of this.root.querySelectorAll('.track-art')) image.dataset.queued='';
    if(release) { for(const url of this.urls) URL.revokeObjectURL(url); this.urls.clear(); }
  }
  watch() {
    this.open=true;
    for(const image of this.root.querySelectorAll('.track-art')) {
      if(image.dataset.loaded) continue;
      if(this.observer) { this.observer.unobserve(image); this.observer.observe(image); }
      else this.enqueue(image);
    }
  }
  enqueue(image) {
    if(!this.open || image.dataset.loaded || image.dataset.queued) return;
    image.dataset.queued='1'; this.observer?.unobserve(image); this.queue.push(image); this.pump();
  }
  pump() {
    while(this.active<2 && this.queue.length) {
      const image=this.queue.shift(), generation=this.generation, signal=this.abort.signal;
      ++this.active;
      fetch('/api/artwork/'+encodeURIComponent(image.dataset.videoId)+'?size=small', {signal,cache:'force-cache',priority:'low'})
        .then(response=>{if(!response.ok) throw Error('No artwork'); return response.blob();})
        .then(blob=>{
          if(generation!==this.generation || !image.isConnected) return;
          const url=URL.createObjectURL(blob); this.urls.add(url);
          image.onload=()=>{image.dataset.loaded='1'; image.parentElement.classList.add('loaded');};
          image.src=url;
        }).catch(()=>{}).finally(()=>{
          if(generation!==this.generation) return;
          image.dataset.queued=''; --this.active; this.pump();
        });
    }
  }
}
