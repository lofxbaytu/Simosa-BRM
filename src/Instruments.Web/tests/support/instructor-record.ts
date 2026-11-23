// 測試輔助:取得教官站目前的練習紀錄 JSON 文字(instructor-app 的 sessionRecord() 經 sessionRecordChunks 串接;
// 與下載/講評報告交接的 Blob 內容相同,見 session-record-export.test.ts)。
import { sessionRecordChunks, type SessionRecord } from '../../src/lib/session-record.js';

export function instructorRecordJson(app: unknown): string {
  return [...sessionRecordChunks((app as { sessionRecord(): SessionRecord }).sessionRecord())].join('');
}
