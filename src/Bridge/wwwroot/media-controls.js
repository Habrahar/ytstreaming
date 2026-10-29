// This is a shared live broadcast: expose track navigation, not seeking within
// the phone's temporary MP3 buffer. Reassert commands when native audio starts.
class PlayerMediaControls {
  constructor(session,Metadata,actions) {
    this.session=session; this.Metadata=Metadata; this.actions=actions; this.metadataKey=null;
    this.register();
  }
  register() {
    if(!this.session) return;
    const handlers={seekbackward:null,seekforward:null,seekto:null,
      play:this.actions.play,pause:this.actions.pause,
      previoustrack:this.actions.previous,nexttrack:this.actions.next};
    for(const [action,handler] of Object.entries(handlers)) {
      // An unsupported action must not stop the remaining registration or UI.
      try { this.session.setActionHandler(action,handler); } catch {}
    }
    try { this.session.setPositionState?.({duration:Infinity,position:0,playbackRate:1}); }
    catch { try {this.session.setPositionState?.();} catch {} }
  }
  update(track,artwork,playbackState) {
    if(!this.session) return;
    if(track?.provider==='youtube') {
      const key=JSON.stringify([track.id,track.title,track.artist,artwork]);
      if(this.Metadata && (key!==this.metadataKey || !this.session.metadata)) {
        try {
          this.session.metadata=new this.Metadata({title:track.title,artist:track.artist,
            artwork:artwork?[{src:artwork}]:[]});
          this.metadataKey=key;
        } catch {}
      }
    } else if(this.metadataKey!==null) {
      try {this.session.metadata=null;} catch {}
      this.metadataKey=null;
    }
    try {this.session.playbackState=playbackState;} catch {}
  }
}
