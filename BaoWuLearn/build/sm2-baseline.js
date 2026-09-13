// 用网页端同一个 sm-crypto 库，为平台公钥建立加密基准
// 目标结构：C1(64B, 无04前缀) || C3(32B) || C2
const { sm2 } = require('sm-crypto');

// 平台前端硬编码的公钥（Base64）
const PUB_B64 = 'BJeYoHWNsf60Vr2wPJWEWRvjH6m5r/JvK7Pww8SdohnwAkHKVy0tikYYOYmuKhR83BUS+duMyjAbVtyXZTfc+jY=';
const PUB_BYTES = Buffer.from(PUB_B64, 'base64');
const PUB_HEX = PUB_BYTES.toString('hex');

console.log('=== 公钥解码 ===');
console.log('Base64 长度 :', PUB_B64.length);
console.log('解码字节数  :', PUB_BYTES.length, PUB_BYTES.length === 65 ? '✓ 65 字节（04||X||Y）' : '✗ 长度异常');
console.log('首字节      :', '0x' + PUB_BYTES[0].toString(16), PUB_BYTES[0] === 0x04 ? '✓ 未压缩点标识' : '✗ 不是 04');
console.log('hex 形式    :', PUB_HEX);
console.log();

function probe(plain) {
  const ct = sm2.doEncrypt(plain, PUB_HEX, 1);   // cipherMode=1 → C1C3C2
  const bytes = Buffer.from(ct, 'hex');
  const expected = 64 + 32 + Buffer.byteLength(plain, 'utf8');
  console.log('─'.repeat(66));
  console.log('明文      :', JSON.stringify(plain), `(${Buffer.byteLength(plain, 'utf8')} 字节 UTF-8)`);
  console.log('密文 hex  :', ct);
  console.log('密文长度  :', ct.length, '字符 =', bytes.length, '字节');
  console.log('预期长度  :', expected, '字节', bytes.length === expected ? '✓' : '✗');
  console.log('C1(前64B) :', ct.slice(0, 128));
  console.log('C3(中32B) :', ct.slice(128, 192));
  console.log('C2(余下)  :', ct.slice(192));
  console.log('C1 无 04  :', bytes[0] === 0x04 ? '✗ 带 04 前缀' : '✓ 无 04 前缀');
  return ct;
}

console.log('=== 加密基准（cipherMode=1 → C1C3C2）===');
const a = probe('test123');
probe('1234');
probe('Aa1B');
probe('中文密码');

const again = sm2.doEncrypt('test123', PUB_HEX, 1);
console.log('─'.repeat(66));
console.log('随机性检查:', again !== a ? '✓ 两次密文不同（随机 k 正常）' : '✗ 相同（异常）');
console.log();
console.log('=== C# 实现自检要点 ===');
console.log('1. 输出总字节数 = 64 + 32 + UTF8明文字节数');
console.log('2. 首字节不是 0x04');
console.log('3. C3（32 字节）位于 [64, 96) 区间');
