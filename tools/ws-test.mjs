// 临时测试：连上 ControlHub 的 WebSocket，打印消息并按一个按钮。
// 用法: node ws-test.mjs "ws://127.0.0.1:8080/ws?t=TOKEN" [pageId] [buttonId]
const [url, pageId = 'default', buttonId = 'b1'] = process.argv.slice(2);
if (!url) { console.error('usage: node ws-test.mjs <ws-url> [pageId] [buttonId]'); process.exit(1); }

const ws = new WebSocket(url);
const seen = [];

ws.onopen = () => console.log('OPEN');
ws.onmessage = (e) => {
  const s = String(e.data);
  seen.push(s);
  console.log('<-', s.length > 240 ? s.slice(0, 240) + '…' : s);
};
ws.onerror = (e) => console.log('ERROR', e.message || e.type);
ws.onclose = (e) => console.log('CLOSE', e.code, e.reason);

setTimeout(() => {
  console.log('-> press', pageId, buttonId);
  ws.send(JSON.stringify({ type: 'press', pageId, buttonId }));
}, 700);

setTimeout(() => {
  console.log('-> goto code');
  ws.send(JSON.stringify({ type: 'goto', pageId: 'code' }));
}, 1200);

setTimeout(() => { ws.close(); }, 3500);
setTimeout(() => process.exit(0), 4000);
