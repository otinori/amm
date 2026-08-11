// PoC: xterm.js + node-pty served over WebSocket.
// Stands in for Tauri's architecture (native shell + OS webview hosting xterm.js).
// Not a Tauri app itself — Tauri would swap this Node/Express/ws layer for a Rust
// process + native window, but the terminal rendering layer (xterm.js) is identical.
const express = require('express');
const { WebSocketServer } = require('ws');
const pty = require('node-pty');
const path = require('path');
const http = require('http');

const PORT = process.env.PORT || 5173;
const app = express();
app.use(express.static(path.join(__dirname, 'public')));

const server = http.createServer(app);
const wss = new WebSocketServer({ server, path: '/pty' });

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

server.listen(PORT, () => console.log(`poc-tauri-terminal listening on http://localhost:${PORT}`));
