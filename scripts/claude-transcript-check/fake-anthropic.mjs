// A fake Anthropic Messages API for the nightly Claude Code transcript check. No model, no secrets: it answers every
// request with a scripted reply, so a real `claude` writes a real transcript that Fleet's reader can be run over.
//
//   node fake-anthropic.mjs [port]      (port 0 picks one; the chosen port is printed as "listening <port>")
//
// The script is read from the newest user message in the request:
//   - a tool_result is last        -> a closing text reply
//   - the prompt contains "TOOL:<path>" -> a tool call (Read of <path>), then the closing reply on the next request
//   - the prompt asks for "a detailed summary" -> a short summary (what /compact asks for)
//   - anything else                -> "Fake reply to: <prompt>"
import http from 'node:http';

const port = Number(process.argv[2] ?? 0);

function lastUserText(messages) {
  // Newest user message: Claude Code may put other roles (system reminders) after it.
  const last = [...messages].reverse().find((m) => m.role === 'user');
  if (!last) return { toolResult: false, text: '' };
  const bare = (t) => t.replace(/<system-reminder>[\s\S]*?<\/system-reminder>/g, '').trim();
  if (typeof last.content === 'string') return { toolResult: false, text: bare(last.content) };
  const blocks = last.content ?? [];
  if (blocks.some((b) => b.type === 'tool_result')) return { toolResult: true, text: '' };
  return { toolResult: false, text: blocks.filter((b) => b.type === 'text').map((b) => bare(b.text)).filter(Boolean).join('\n') };
}

function script(body) {
  const { toolResult, text } = lastUserText(body.messages ?? []);
  if (toolResult) return [{ type: 'text', text: 'The file was read. All done here.' }];
  const tool = /TOOL:(\S+)/.exec(text);
  if (tool && (body.tools ?? []).some((t) => t.name === 'Read')) {
    return [
      { type: 'text', text: 'Reading the file.' },
      { type: 'tool_use', id: `toolu_fake${Date.now().toString(36)}`, name: 'Read', input: { file_path: tool[1] } },
    ];
  }
  if (/detailed summary/i.test(text)) {
    return [{ type: 'text', text: '<summary>The user asked for plain replies and one file read; both were done.</summary>' }];
  }
  return [{ type: 'text', text: `Fake reply to: ${text.slice(0, 60).replace(/\s+/g, ' ')}` }];
}

const usage = (out) => ({ input_tokens: 10, output_tokens: out, cache_creation_input_tokens: 0, cache_read_input_tokens: 0 });

function messageJson(body, blocks) {
  return {
    id: `msg_fake${Math.random().toString(36).slice(2, 10)}`,
    type: 'message',
    role: 'assistant',
    model: body.model ?? 'fake',
    content: blocks,
    stop_reason: blocks.some((b) => b.type === 'tool_use') ? 'tool_use' : 'end_turn',
    stop_sequence: null,
    usage: usage(5),
  };
}

function stream(res, body, blocks) {
  const msg = messageJson(body, []);
  res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
  const send = (event, data) => res.write(`event: ${event}\ndata: ${JSON.stringify({ type: event, ...data })}\n\n`);
  send('message_start', { message: { ...msg, usage: usage(1) } });
  blocks.forEach((block, index) => {
    if (block.type === 'text') {
      send('content_block_start', { index, content_block: { type: 'text', text: '' } });
      send('content_block_delta', { index, delta: { type: 'text_delta', text: block.text } });
    } else {
      send('content_block_start', { index, content_block: { type: 'tool_use', id: block.id, name: block.name, input: {} } });
      send('content_block_delta', { index, delta: { type: 'input_json_delta', partial_json: JSON.stringify(block.input) } });
    }
    send('content_block_stop', { index });
  });
  send('message_delta', {
    delta: { stop_reason: blocks.some((b) => b.type === 'tool_use') ? 'tool_use' : 'end_turn', stop_sequence: null },
    usage: usage(5),
  });
  send('message_stop', {});
  res.end();
}

const server = http.createServer((req, res) => {
  let raw = '';
  req.on('data', (c) => (raw += c));
  req.on('end', () => {
    const url = req.url ?? '';
    let body = {};
    try { body = JSON.parse(raw || '{}'); } catch { /* not JSON */ }
    if (url.includes('count_tokens')) {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ input_tokens: 10 }));
      return;
    }
    if (url.startsWith('/v1/messages')) {
      const blocks = script(body);
      console.log(`${req.method} ${url} stream=${!!body.stream} -> ${blocks.map((b) => b.type).join('+')}`);
      if (body.stream) return stream(res, body, blocks);
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify(messageJson(body, blocks)));
      return;
    }
    console.log(`${req.method} ${url} -> 404`);
    res.writeHead(404, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ type: 'error', error: { type: 'not_found_error', message: 'not here' } }));
  });
});

server.listen(port, '127.0.0.1', () => console.log(`listening ${server.address().port}`));
