import {createInterface} from 'node:readline';
import path from 'node:path';
import {Monitor,loadSettings,validateSettings,saveJson,runtimeDir} from './core.mjs';

// Parent-owned IPC: no HTTP server, API credentials, or raw upstream responses reach the UI.
const monitor=new Monitor(await loadSettings());
let timer,watcher,closed=false,watching=false,lastSnapshot='';
const send=(type,data)=>{if(!closed)process.stdout.write(JSON.stringify({type,data})+'\n');};
function publish(){
  const snapshot=monitor.snapshot(),signature=JSON.stringify(snapshot);
  if(signature!==lastSnapshot){lastSnapshot=signature;send('snapshot',snapshot);}
}
function schedule(){clearInterval(timer);timer=setInterval(()=>{void refresh();},monitor.settings.intervalSeconds*1000);}
async function refresh(){
  try {const pending=monitor.refresh();publish();await pending;publish();}
  catch {send('error','余额查询失败，请稍后刷新');}
}
async function watch(){
  if(watching||closed)return;
  watching=true;
  try {
    const pending=monitor.checkForChanges();publish();await pending;publish();
  }catch{send('error','无法更新 CC Switch 配置，请稍后重试');}
  finally{watching=false;}
}
function shutdown(){closed=true;clearInterval(timer);clearInterval(watcher);process.exit(0);}
process.stdout.on('error',shutdown);
const input=createInterface({input:process.stdin,crlfDelay:Infinity});
input.on('close',shutdown);
let queue=Promise.resolve();
input.on('line',line=>{
  if(line.length>2*1024*1024){send('error','设置数据过大');return;}
  queue=queue.then(async()=>{
    try {
      const request=JSON.parse(line);
      if(!request||typeof request!=='object'||Array.isArray(request))throw new Error('invalid');
      if(request.method==='refresh')await refresh();
      else if(request.method==='detect'){
        const pending=monitor.retryDetection(request.providerId);publish();await pending;publish();
      }
      else if(request.method==='status')send('snapshot',monitor.snapshot());
      else if(request.method==='settings'){
        const incoming=validateSettings(request.settings);
        const settings=validateSettings({intervalSeconds:incoming.intervalSeconds,
          thresholds:{...monitor.settings.thresholds,...incoming.thresholds},
          adapters:{...monitor.settings.adapters,...incoming.adapters}});
        await saveJson(path.join(runtimeDir,'settings.json'),settings);
        monitor.settings=settings;schedule();send('settings_saved',monitor.snapshot());void watch();
      }else if(request.method==='stop')shutdown();
      else throw new Error('invalid');
    }catch{send('error','操作失败，请检查设置后重试');}
  });
});
schedule();
watcher=setInterval(()=>{void watch();},5000);
void refresh();
