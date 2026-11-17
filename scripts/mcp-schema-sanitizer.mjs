#!/usr/bin/env node
/**
 * MCP schema 清洗代理（stdio → stdio），dsh++ 内嵌资产。
 *
 * 背景：部分 MCP server（如 rea-agents）的工具 inputSchema 含 pattern
 * "^[^\\uD800-\\uDFFF]*$" 一类的 surrogate 转义，DeepSeek API 的工具校验器
 * 不接受该写法，会拒绝整轮带工具的请求（"… is not a 'regex'"）。
 *
 * 代理包裹真实 MCP 子进程：请求原样转发；tools/list 响应中的 inputSchema
 * 递归删除不被接受的 surrogate-range pattern，其余字节不动。server 升级 /
 * 重装后依然生效，不需要修改 server 任何文件。
 *
 * 两种用法：
 *   node mcp-schema-sanitizer.mjs <server-entry.mjs> [server 原参数…]   （node 脚本）
 *   node mcp-schema-sanitizer.mjs --exec <command> [args…]              （任意命令）
 */
import { spawn } from 'node:child_process';

const argv = process.argv.slice(2);
let command, args;
if (argv[0] === '--exec') {
  [, command, ...args] = argv;
} else {
  command = process.execPath;
  args = argv;
}
if (!command) {
  console.error('usage: node mcp-schema-sanitizer.mjs <entry> [args…] | --exec <command> [args…]');
  process.exit(2);
}

const child = spawn(command, args, { stdio: ['pipe', 'pipe', 'inherit'], env: process.env });

function sanitize(node) {
  if (Array.isArray(node)) {
    for (const item of node) sanitize(item);
    return node;
  }
  if (node && typeof node === 'object') {
    for (const key of Object.keys(node)) {
      if (key === 'pattern' && typeof node[key] === 'string' && /\\u[dD][89abAB][0-9a-fA-F]{2}/i.test(node[key])) {
        delete node[key];
      } else {
        sanitize(node[key]);
      }
    }
  }
  return node;
}

let buffer = '';
child.stdout.on('data', chunk => {
  buffer += chunk.toString('utf8');
  let index;
  while ((index = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, index);
    buffer = buffer.slice(index + 1);
    if (!line.trim()) continue;
    try {
      const message = JSON.parse(line);
      if (message && message.result && Array.isArray(message.result.tools))
        for (const tool of message.result.tools)
          if (tool && tool.inputSchema) sanitize(tool.inputSchema);
      process.stdout.write(JSON.stringify(message) + '\n');
    } catch {
      process.stdout.write(line + '\n'); // 非 JSON 行原样透传
    }
  }
});
child.stdout.on('end', () => {
  if (buffer.trim()) process.stdout.write(buffer + '\n');
  process.exit(0);
});
child.on('exit', code => process.exit(code ?? 0));
child.on('error', error => {
  console.error('mcp-schema-sanitizer: 无法启动子进程：' + error.message);
  process.exit(1);
});

process.stdin.on('data', chunk => child.stdin.write(chunk));
process.stdin.on('end', () => child.stdin.end());
process.on('SIGINT', () => { child.kill('SIGINT'); process.exit(130); });
process.on('SIGTERM', () => { child.kill('SIGTERM'); process.exit(143); });
