// Minimal stdio JSON-RPC MCP server fixture for mcp-gateway verification
// (phase 8.2). Implements just enough of the protocol (initialize,
// tools/list with one "echo" tool, tools/call) for GatewayManager to reach
// Running status and forward a real tools/call - no network/npm registry
// dependency, so it works offline and its behavior is fully controlled.
const readline = require('readline');
const rl = readline.createInterface({ input: process.stdin });

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

rl.on('line', (line) => {
  if (!line.trim()) return;
  let req;
  try { req = JSON.parse(line); } catch { return; }
  const { id, method, params } = req;
  if (method === 'initialize') {
    send({ jsonrpc: '2.0', id, result: { protocolVersion: '2024-11-05', capabilities: { tools: {} }, serverInfo: { name: 'fixture-mcp-server', version: '0.1' } } });
  } else if (method === 'tools/list') {
    send({ jsonrpc: '2.0', id, result: { tools: [{ name: 'echo', description: 'Echoes back its input', inputSchema: { type: 'object', properties: { text: { type: 'string' } } } }] } });
  } else if (method === 'tools/call') {
    const text = params?.arguments?.text ?? '';
    send({ jsonrpc: '2.0', id, result: { content: [{ type: 'text', text: `echo: ${text}` }], isError: false } });
  } else if (id !== undefined) {
    send({ jsonrpc: '2.0', id, error: { code: -32601, message: `Method not found: ${method}` } });
  }
});
