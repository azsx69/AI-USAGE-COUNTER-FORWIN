# AI Usage Counter — Windows (Claude + Codex + Gemini)

Windows tray port of the macOS *Claude Usage Counter*. แสดง usage ของ AI providers
(session 5 ชั่วโมง + weekly) ที่ system tray โดยยืม session cookie จาก WebView2
แล้วเรียก internal API / scrape หน้า usage ของแต่ละเจ้า เหมือนต้นฉบับ macOS

รองรับ:
- **Claude** (claude.ai) — internal JSON API + local `.jsonl` fallback
- **Codex/ChatGPT** (chatgpt.com) — `wham/usage` API + scrape fallback
- **Gemini** (gemini.google.com) — DOM scraping หน้า Usage Limits

## Requirements
- Windows 10/11
- .NET 9 SDK (สำหรับ build)
- WebView2 Runtime (มากับ Windows 11 / Edge อยู่แล้ว)

## Build & Run
```powershell
dotnet build -c Release
.\bin\Release\net9.0-windows\AiUsageCounter.exe
```
หรือระหว่างพัฒนา: `dotnet run`

## วิธีใช้
1. รันแล้วจะมีไอคอนตัวเลขขึ้นที่ system tray (มุมขวาล่าง)
2. **คลิกขวา** → เลือก `Claude: Sign in` หรือ `Codex: Sign in` → หน้าต่าง login เปิด
   ให้ login ตามปกติ หน้าต่างจะปิดเองเมื่อสำเร็จ (cookie เก็บถาวรแยกต่อ provider)
3. **คลิกซ้าย** ที่ไอคอน → popup แสดงแถบ session / weekly ของทุก provider แบบ stack
4. **คลิกขวา → "Show in tray"** → เลือกว่าตัวเลขบนไอคอนมาจาก provider ไหน
5. ตัวเลขบนไอคอน = % การใช้งาน session ของ provider ที่เลือก (แดง = MAX)

เฉพาะ Claude: ถ้ายังไม่ login จะ fallback ไปประมาณการจากไฟล์ local ของ Claude Code
(`%USERPROFILE%\.claude\projects\*.jsonl`) โดยอัตโนมัติ — popup จะระบุ "Local estimate"

## โครงสร้างโค้ด
| ไฟล์ | หน้าที่ |
|---|---|
| `Program.cs` | entry point + single-instance guard |
| `TrayContext.cs` | tray icon, fetch loop, file watcher, popup |
| `WebViewHost.cs` | WebView2 ซ่อน — login + รัน JS fetch + cookie store |
| `ClaudeProvider.cs` | auth + เรียก usage API ของ claude.ai |
| `LocalUsageParser.cs` | อ่าน `.claude/projects/*.jsonl` (offline fallback) |
| `PopupForm.cs` | popup แสดงแถบ usage |
| `Models.cs` | data models |

## ข้อมูล / ความปลอดภัย
- Cookie ของ claude.ai เก็บโดย WebView2 ที่
  `%LOCALAPPDATA%\AiUsageCounter\WebView2\claude` (ในเครื่องเท่านั้น ไม่ส่งออกที่ไหน)
- JS ที่ inject เป็น static ทั้งหมด, ทุก request เป็น HTTPS ไป claude.ai โดยตรง
