// 語音腳本播放:有 file → audio;音檔失敗/無 file → speechSynthesis(假物件);皆無 → 只顯示;播放紀錄。
import { describe, expect, it } from 'vitest';
import { VoiceScriptPlayer, voiceLine, voiceRecordLabel, type AudioLike, type SpeechLike } from '../src/lib/voice-script.js';
import type { SimEvent } from '../src/types/event.js';

class FakeAudio implements AudioLike {
  static instances: FakeAudio[] = [];
  onended: (() => void) | null = null;
  onerror: (() => void) | null = null;
  paused = false;
  constructor(
    public src: string,
    private readonly fail: 'none' | 'blocked' | 'error' = 'none',
  ) {
    FakeAudio.instances.push(this);
  }
  play(): Promise<void> {
    if (this.fail === 'blocked') {
      const err = new Error('user gesture required');
      err.name = 'NotAllowedError';
      return Promise.reject(err);
    }
    if (this.fail === 'error') return Promise.reject(new Error('decode'));
    return Promise.resolve();
  }
  pause(): void {
    this.paused = true;
  }
}

class FakeSpeech implements SpeechLike {
  spoken: Array<{ text: string; lang: string }> = [];
  cancelled = 0;
  speak(text: string, lang: string, onend: () => void): void {
    this.spoken.push({ text, lang });
    setTimeout(onend, 0);
  }
  cancel(): void {
    this.cancelled++;
  }
}

function voiceEvent(data: Record<string, unknown>, itemId = 'ecrCheck'): SimEvent {
  return { kind: 'voiceScript', tick: 15000, t: 300, source: 'timeline', itemId, visible: false, seq: 3, phase: 'normal', data };
}

const now = () => new Date(Date.UTC(2026, 9, 17, 1, 2, 3));

describe('VoiceScriptPlayer', () => {
  it('有 file 且找得到音檔 → audio,紀錄 status audio 與事件時間', async () => {
    FakeAudio.instances = [];
    const speech = new FakeSpeech();
    const player = new VoiceScriptPlayer({ audioFactory: (url) => new FakeAudio(url), speech, resolveFile: (id, file) => `/bundled/${id}/${file}`, now });
    const rec = await player.play(voiceEvent({ role: 'pilot', channel: 'bridge', text: '引水人登船', file: 'audio/E07/pilot.ogg' }, 'pilotBoarded'), 'E07_steering_failure', false);
    expect(FakeAudio.instances[0]!.src).toBe('/bundled/E07_steering_failure/audio/E07/pilot.ogg');
    expect(rec).toEqual({ t: 300, tick: 15000, itemId: 'pilotBoarded', role: 'pilot', channel: 'bridge', text: '引水人登船', file: 'audio/E07/pilot.ogg', lang: 'zh-TW', status: 'audio', auto: false, wall: '2026-10-17T01:02:03.000Z' });
    expect(player.playing).toBe(true);
    expect(speech.spoken).toEqual([]);
    FakeAudio.instances[0]!.onended?.();
    expect(player.playing).toBe(false);
    expect(player.all()).toHaveLength(1);
  });

  it('無 file → speechSynthesis(語言依 lang);音檔載入失敗退回 TTS', async () => {
    const speech = new FakeSpeech();
    const player = new VoiceScriptPlayer({ audioFactory: (url) => new FakeAudio(url, 'error'), speech, resolveFile: () => '/x.ogg', now });
    const tts = await player.play(voiceEvent({ role: 'ecr', channel: 'phone', text: 'Bridge, ECR.', lang: 'en-US' }), 'E01', true);
    expect(tts!.status).toBe('tts');
    expect(tts!.auto).toBe(true);
    expect(speech.spoken).toEqual([{ text: 'Bridge, ECR.', lang: 'en-US' }]);
    const fallback = await player.play(voiceEvent({ role: 'ecr', channel: 'phone', text: '駕駛台', file: 'missing.ogg' }), 'E01');
    expect(fallback!.status).toBe('tts');
    expect(speech.spoken[1]).toEqual({ text: '駕駛台', lang: 'zh-TW' });
    expect(speech.cancelled).toBeGreaterThan(0); // 新播放前先停前一則
  });

  it('瀏覽器阻擋自動播放 → blocked;無 TTS 與無文字 → text;非語音事件 → null', async () => {
    const blocked = new VoiceScriptPlayer({ audioFactory: (url) => new FakeAudio(url, 'blocked'), speech: null, resolveFile: () => '/x.ogg', now });
    expect((await blocked.play(voiceEvent({ role: 'vts', channel: 'vhf13', file: 'a.ogg' }), 'E01'))!.status).toBe('blocked');
    const textOnly = new VoiceScriptPlayer({ audioFactory: (url) => new FakeAudio(url), speech: null, resolveFile: () => null, now });
    expect(textOnly.canSpeak).toBe(false);
    expect((await textOnly.play(voiceEvent({ role: 'vts', channel: 'vhf13', text: 'hello' }), 'E01'))!.status).toBe('text');
    expect(await textOnly.play({ kind: 'message', tick: 0, t: 0, source: 'timeline', visible: false, seq: 0 }, 'E01')).toBeNull();
  });

  it('顯示文字與操作紀錄標籤', () => {
    expect(voiceLine({ role: 'pilot', channel: 'bridge', text: '船長早' })).toBe('引水人:船長早');
    expect(voiceLine({ role: 'unknownRole', channel: 'bridge', file: 'a.ogg' })).toBe('unknownRole:(音檔 a.ogg)');
    expect(voiceRecordLabel({ t: 1, tick: 50, role: 'ecr', channel: 'phone', text: '駕駛台', lang: 'zh-TW', status: 'tts', auto: true, wall: '' })).toBe('語音 機控室(TTS,自動):駕駛台');
    expect(voiceRecordLabel({ t: 1, tick: 50, role: 'vts', channel: 'vhf13', text: '', file: 'v.ogg', lang: 'zh-TW', status: 'audio', auto: false, wall: '' })).toBe('語音 VTS(音檔):v.ogg');
  });
});
