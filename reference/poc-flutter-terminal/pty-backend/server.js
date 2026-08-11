// Same-shape pty backend as reference/poc-tauri-terminal/server.js so both PoCs
// exercise an identical real-shell backend; the only variable under test is the
// terminal rendering client (xterm.js there vs xterm.dart here).
const { WebSocketServer } = require('ws');
const pty = require('node-pty');

const PORT = process.env.PORT || 5174;
const wss = new WebSocketServer({ port: PORT });

wss.on('connection', (ws) => {
  const shell = process.env.SHELL || 'bash';
  const term = pty.spawn(shell, [], {
    name: 'xterm-256color',
    cols: 100,
    rows: 30,
    cwd: process.env.HOME,
    env: process.env,
  });

  term.onData((data) => {
    if (ws.readyState === ws.OPEN) ws.send(JSON.stringify({ type: 'data', data }));
  });

  ws.on('message', (msg) => {
    const parsed = JSON.parse(msg.toString());
    if (parsed.type === 'input') term.write(parsed.data);
    if (parsed.type === 'resize') term.resize(parsed.cols, parsed.rows);
  });

  ws.on('close', () => term.kill());
});

console.log(`poc-flutter-terminal pty-backend listening on ws://localhost:${PORT}`);
