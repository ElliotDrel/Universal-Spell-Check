import fetch from 'node-fetch';

const input = await new Promise(resolve => {
  let data = '';
  process.stdin.on('data', chunk => data += chunk);
  process.stdin.on('end', () => resolve(data.trim()));
});

const res = await fetch('https://api.openai.com/v1/chat/completions', {
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    Authorization: 'Bearer REDACTED'
  },
  body: JSON.stringify({
    model: 'gpt-4.1',
    messages: [
      { role: 'system', content: 'Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Return only the corrected text.' },
      { role: 'user', content: `<Text> ${input} </Text>` }
    ],
    temperature: 0.1
  })
});

const corrected = (await res.json()).choices[0].message.content;
process.stdout.write(corrected); 