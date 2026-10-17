// 語音腳本播放(設計文件 docs/design/scenario-engine.md 第 6 節 W4、規劃書第 9.1 節「語音腳本」):
// 核心只記錄 voiceScript 事件,由教官站播放 —— 有 file 時用 <audio> 播情境包的音檔
// (data/scenarios/<id>/<file>,建置時以 import.meta.glob 打包;找不到或載入失敗時退回 TTS),
// 無檔或無音檔時用 speechSynthesis(語言依 lang,預設 zh-TW)。簡化教官模式自動播放,完整教官站顯示「播放」鈕;
// 每次播放回傳一筆紀錄,由教官站寫入 SessionRecord.operations(kind voiceScript)。
// 瀏覽器可能需要使用者先互動才允許自動播放;失敗時紀錄 status = 'blocked',畫面仍顯示文字。

import type { SimEvent, VoiceScriptData } from '../types/event.js';
import { ROLE_LABEL, voiceScriptData } from './sim-event.js';

export type VoicePlayStatus = 'audio' | 'tts' | 'text' | 'blocked' | 'failed';

export interface VoicePlayRecord {
  /** 事件的模擬時間 */
  t: number;
  tick: number;
  itemId?: string;
  role: string;
  channel: string;
  text: string;
  file?: string;
  lang: string;
  status: VoicePlayStatus;
  /** 自動播放(簡化教官模式)或教官手動按「播放」 */
  auto: boolean;
  /** 本地時鐘 */
  wall: string;
}

/** 可注入的播放器介面(測試用假物件;瀏覽器用 HTMLAudioElement 與 speechSynthesis)。 */
export interface AudioLike {
  src: string;
  play(): Promise<void>;
  pause(): void;
  onended: (() => void) | null;
  onerror: (() => void) | null;
}

export interface SpeechLike {
  speak(text: string, lang: string, onend: () => void, onerror: () => void): void;
  cancel(): void;
}

export interface VoiceScriptPlayerOptions {
  audioFactory?: (url: string) => AudioLike;
  speech?: SpeechLike | null;
  /** 音檔 URL 解析(預設:建置時打包的 data/scenarios/<id>/<file>;找不到回傳 null) */
  resolveFile?: (scenarioId: string, file: string) => string | null;
  now?: () => Date;
  /** 預設語言(lang 未給時) */
  defaultLang?: string;
}

const AUDIO_FILES = import.meta.glob('@data/scenarios/*/**/*.{ogg,mp3,wav,m4a,opus}', { query: '?url', import: 'default', eager: true }) as Record<string, string>;

/** 預設音檔解析:在建置時打包的清單中找 …/scenarios/<id>/<file>。 */
export function resolveBundledAudio(scenarioId: string, file: string): string | null {
  const want = `/scenarios/${scenarioId}/${file.replace(/\\/g, '/').replace(/^\.?\//, '')}`;
  for (const [key, url] of Object.entries(AUDIO_FILES)) {
    if (key.replace(/\\/g, '/').endsWith(want)) return url;
  }
  return null;
}

function browserAudioFactory(url: string): AudioLike {
  const a = new Audio(url);
  return a as unknown as AudioLike;
}

function browserSpeech(): SpeechLike | null {
  if (typeof speechSynthesis === 'undefined' || typeof SpeechSynthesisUtterance === 'undefined') return null;
  return {
    speak(text, lang, onend, onerror) {
      const u = new SpeechSynthesisUtterance(text);
      u.lang = lang;
      u.onend = onend;
      u.onerror = onerror;
      speechSynthesis.speak(u);
    },
    cancel() {
      speechSynthesis.cancel();
    },
  };
}

export class VoiceScriptPlayer {
  private readonly audioFactory: (url: string) => AudioLike;
  private readonly speech: SpeechLike | null;
  private readonly resolveFile: (scenarioId: string, file: string) => string | null;
  private readonly now: () => Date;
  private readonly defaultLang: string;
  private current: AudioLike | null = null;
  private readonly records: VoicePlayRecord[] = [];
  private _playing = false;
  private listeners = new Set<(playing: boolean) => void>();

  constructor(options: VoiceScriptPlayerOptions = {}) {
    this.audioFactory = options.audioFactory ?? browserAudioFactory;
    this.speech = options.speech === undefined ? browserSpeech() : options.speech;
    this.resolveFile = options.resolveFile ?? resolveBundledAudio;
    this.now = options.now ?? (() => new Date());
    this.defaultLang = options.defaultLang ?? 'zh-TW';
  }

  get playing(): boolean {
    return this._playing;
  }

  /** 播放狀態變化(畫面顯示「播放中」) */
  onPlaying(listener: (playing: boolean) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  all(): readonly VoicePlayRecord[] {
    return this.records;
  }

  /** 是否有任何播放方式(音檔或 TTS);皆無時只顯示文字。 */
  get canSpeak(): boolean {
    return this.speech !== null;
  }

  /**
   * 播放一則 voiceScript 事件。回傳紀錄(含實際播放方式);非 voiceScript 事件回傳 null。
   * 有 file 且找得到 → audio;audio 載入失敗 → TTS;無 TTS → text(只顯示)。
   */
  async play(event: SimEvent, scenarioId: string, auto = false): Promise<VoicePlayRecord | null> {
    const v = voiceScriptData(event);
    if (!v) return null;
    const lang = v.lang ?? this.defaultLang;
    const text = v.text ?? '';
    const base: VoicePlayRecord = { t: event.t, tick: event.tick, role: v.role, channel: v.channel, text, lang, status: 'text', auto, wall: this.now().toISOString() };
    if (event.itemId) base.itemId = event.itemId;
    if (v.file) base.file = v.file;
    this.stop();
    let status: VoicePlayStatus = 'text';
    const url = v.file ? this.resolveFile(scenarioId, v.file) : null;
    if (url) {
      status = await this.playAudio(url);
      if (status === 'failed' && text) status = await this.speak(text, lang);
    } else if (text) {
      status = await this.speak(text, lang);
    }
    const record: VoicePlayRecord = { ...base, status };
    this.records.push(record);
    return record;
  }

  stop(): void {
    if (this.current) {
      try {
        this.current.pause();
      } catch {
        /* 忽略 */
      }
      this.current = null;
    }
    this.speech?.cancel();
    this.setPlaying(false);
  }

  private async playAudio(url: string): Promise<VoicePlayStatus> {
    let audio: AudioLike;
    try {
      audio = this.audioFactory(url);
    } catch {
      return 'failed';
    }
    this.current = audio;
    audio.onended = () => {
      if (this.current === audio) this.current = null;
      this.setPlaying(false);
    };
    audio.onerror = () => {
      if (this.current === audio) this.current = null;
      this.setPlaying(false);
    };
    try {
      await audio.play();
      this.setPlaying(true);
      return 'audio';
    } catch (err) {
      this.current = null;
      // NotAllowedError:瀏覽器要求先有使用者互動
      return err instanceof Error && err.name === 'NotAllowedError' ? 'blocked' : 'failed';
    }
  }

  private speak(text: string, lang: string): Promise<VoicePlayStatus> {
    if (!this.speech) return Promise.resolve('text');
    return new Promise((resolve) => {
      try {
        this.speech!.speak(
          text,
          lang,
          () => this.setPlaying(false),
          () => this.setPlaying(false),
        );
        this.setPlaying(true);
        resolve('tts');
      } catch {
        resolve('failed');
      }
    });
  }

  private setPlaying(p: boolean): void {
    if (this._playing === p) return;
    this._playing = p;
    for (const l of this.listeners) l(p);
  }
}

/** 語音事件的顯示文字:「引水人(駕駛台):…」。 */
export function voiceLine(v: VoiceScriptData): string {
  return `${ROLE_LABEL[v.role] ?? v.role}:${v.text ?? (v.file ? `(音檔 ${v.file})` : '')}`;
}

/** 播放紀錄 → 操作紀錄標籤。 */
export function voiceRecordLabel(r: VoicePlayRecord): string {
  const how = r.status === 'audio' ? '音檔' : r.status === 'tts' ? 'TTS' : r.status === 'blocked' ? '被瀏覽器阻擋' : r.status === 'failed' ? '失敗' : '僅顯示';
  return `語音 ${ROLE_LABEL[r.role] ?? r.role}(${how}${r.auto ? ',自動' : ''}):${r.text || r.file || ''}`;
}
