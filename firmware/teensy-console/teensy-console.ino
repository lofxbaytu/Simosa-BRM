// Simosa BRM 精簡版操控台 — Teensy 4.0 參考韌體(規劃書第 5.4 節;協定 src/Contracts/console-serial.md)
// ---------------------------------------------------------------------------------------------------
// 功能:舵輪電位計 → RUD、車鐘檔位(電阻梯)→ TEL、側推電位計 → THR、按鈕(ACK/SILENCE/FU/NFU/EMERG/BNWAS)→ BTN、
//       非隨動推桿 → NFU、1 Hz HB;接收主機的 ACK / LED / DISP / BUZZ。USB CDC(Serial)115200 8N1、ASCII、每行 \n、
//       每行附 NMEA 式 *XX 校驗和(HELLO 宣告 CS,主機之後的行亦必帶,錯誤即丟棄)。
// 接腳(ASSUMED,依實體製作時的接線調整;見 README.md 接腳表):
//   A0  舵輪電位計(10 kΩ 線性,中點 = 舵中;左舵 −35°、右舵 +35°)
//   A1  側推電位計(中點 = 0;正 = 推向右舷)
//   A2  車鐘檔位(9 段旋轉開關接電阻梯:0 V = FAS … 3.3 V = FAH,門檻見 TELEGRAPH_LEVELS)
//   2   按鈕 ACK      3  按鈕 SILENCE   4  按鈕 FU   5  按鈕 NFU   6  按鈕 EMERG   7  按鈕 BNWAS(INPUT_PULLUP,按下接地)
//   8   NFU 推桿左    9  NFU 推桿右(INPUT_PULLUP,推到底接地;都未接地 = 放開)
//   14  LED FU   15  LED NFU   16  LED AUTO   17  LED EMERG   18  LED ALARM   19  LED BNWAS   20  LED LINK(經 330 Ω 到 GND)
//   21  蜂鳴器(主動式,高電位響)
//   DISP:本參考韌體把顯示值送到 displayField();預設只保存值(可改接 I²C LCD / 七段顯示模組)。
// 無法在無硬體的容器編譯:以 Arduino IDE 2.x + Teensyduino(Board: Teensy 4.0,USB Type: Serial)編譯燒錄並以 README.md 的步驟驗證。
// 不依賴任何外部函式庫(去抖、校驗和自行實作),完全離線。

#include <Arduino.h>

static const char FW_VERSION[] = "1.0.0";
static const char CAPS[] = "RUD+TEL+THR+BTN+NFU+LED+DISP+BUZZ+CS";

// ---- 接腳(ASSUMED)----
static const int PIN_RUDDER = A0;
static const int PIN_THRUSTER = A1;
static const int PIN_TELEGRAPH = A2;
static const int BUTTON_PINS[] = { 2, 3, 4, 5, 6, 7 };
static const char* BUTTON_NAMES[] = { "ACK", "SILENCE", "FU", "NFU", "EMERG", "BNWAS" };
static const int BUTTON_COUNT = 6;
static const int PIN_NFU_LEFT = 8;
static const int PIN_NFU_RIGHT = 9;
static const char* LED_NAMES[] = { "FU", "NFU", "AUTO", "EMERG", "ALARM", "BNWAS", "LINK" };
static const int LED_PINS[] = { 14, 15, 16, 17, 18, 19, 20 };
static const int LED_COUNT = 7;
static const int PIN_BUZZER = 21;

// ---- 參數 ----
static const float RUDDER_LIMIT_DEG = 35.0f;
static const float RUDDER_SEND_DELTA_DEG = 0.25f;     // 變化 ≥ 0.25° 才送
static const unsigned long RUDDER_SEND_PERIOD_MS = 500; // 或每 500 ms 重送一次
static const float THRUSTER_SEND_DELTA_PCT = 1.0f;
static const unsigned long THRUSTER_SEND_PERIOD_MS = 500;
static const unsigned long BUTTON_DEBOUNCE_MS = 20;
static const unsigned long HELLO_RETRY_MS = 2000;       // 未收到 ACK 每 2 s 重送 HELLO
static const unsigned long HEARTBEAT_MS = 1000;         // 1 Hz HB
static const unsigned long BLINK_HALF_PERIOD_MS = 250;  // LED blink 2 Hz
static const int ADC_BITS = 12;
static const int ADC_MAX = (1 << ADC_BITS) - 1;
static const int ADC_AVERAGING = 16;
static const float POT_DEADBAND_DEG = 0.3f;             // 中點死區(避免舵中附近抖動)

// 車鐘檔位電阻梯:9 段均分 0–ADC_MAX,由低到高 FAS … FAH(ASSUMED;依實際電阻梯校正門檻)
static const char* TELEGRAPH_ORDERS[] = { "FAS", "HAS", "SAS", "DSAS", "STOP", "DSAH", "SAH", "HAH", "FAH" };
static const int TELEGRAPH_COUNT = 9;
static const int TELEGRAPH_HYSTERESIS = ADC_MAX / (TELEGRAPH_COUNT * 6);

// ---- 狀態 ----
static bool acked = false;
static unsigned long lastHelloMs = 0;
static unsigned long lastHeartbeatMs = 0;
static float lastRudderSent = 1000.0f;
static unsigned long lastRudderSentMs = 0;
static float lastThrusterSent = 1000.0f;
static unsigned long lastThrusterSentMs = 0;
static int telegraphIndex = -1;
static bool buttonStable[BUTTON_COUNT];
static bool buttonLastRaw[BUTTON_COUNT];
static unsigned long buttonChangedMs[BUTTON_COUNT];
static char nfuState = '0';
static unsigned long nfuChangedMs = 0;
static char nfuLastRaw = '0';
// LED:0 滅、1 亮、2 閃
static uint8_t ledMode[LED_COUNT];
static unsigned long buzzUntilMs = 0;
static char rxLine[128];
static size_t rxLen = 0;
// DISP 值(供顯示模組)
static char dispRud[12] = "";
static char dispRpm[12] = "";
static char dispHdg[12] = "";
static char dispTel[12] = "";

// ---- 校驗和與送出 ----
static uint8_t checksumOf(const char* payload) {
  uint8_t x = 0;
  for (const char* p = payload; *p; ++p) x ^= (uint8_t)*p;
  return x;
}

static void sendLine(const char* payload) {
  char buf[160];
  snprintf(buf, sizeof(buf), "%s*%02X\n", payload, checksumOf(payload));
  Serial.write(buf);
}

static void sendHello() {
  char buf[96];
  snprintf(buf, sizeof(buf), "HELLO,%s,%s", FW_VERSION, CAPS);
  sendLine(buf);
  lastHelloMs = millis();
}

static void sendFloat(const char* cmd, float value) {
  char num[16];
  dtostrf(value, 0, 2, num);           // 兩位小數;主機容許小數
  char buf[48];
  snprintf(buf, sizeof(buf), "%s,%s", cmd, num);
  sendLine(buf);
}

// ---- 顯示 hook(預設只保存;接 LCD/七段時在此更新模組)----
static void displayField(const char* field, const char* value) {
  char* dst = nullptr;
  if (!strcmp(field, "RUD")) dst = dispRud;
  else if (!strcmp(field, "RPM")) dst = dispRpm;
  else if (!strcmp(field, "HDG")) dst = dispHdg;
  else if (!strcmp(field, "TEL")) dst = dispTel;
  if (dst) { strncpy(dst, value, 11); dst[11] = 0; }
}

// ---- 主機 → 裝置 ----
static void handleHostLine(char* line) {
  // 去尾端 CR 與空白
  size_t n = strlen(line);
  while (n > 0 && (line[n - 1] == '\r' || line[n - 1] == ' ')) line[--n] = 0;
  if (n == 0 || line[0] == '#') return;
  // 校驗和(主機在 CS 下一律附上;缺少或錯誤即丟棄)
  char* star = strrchr(line, '*');
  if (!star) return;
  *star = 0;
  uint8_t given = (uint8_t)strtoul(star + 1, nullptr, 16);
  if (given != checksumOf(line)) return;
  // 欄位
  char* f[4] = { nullptr, nullptr, nullptr, nullptr };
  int fc = 0;
  for (char* tok = strtok(line, ","); tok && fc < 4; tok = strtok(nullptr, ",")) f[fc++] = tok;
  if (fc == 0) return;
  for (char* p = f[0]; *p; ++p) *p = toupper(*p);
  if (!strcmp(f[0], "ACK")) { acked = true; return; }
  if (!strcmp(f[0], "LED") && fc >= 3) {
    for (int i = 0; i < LED_COUNT; ++i) {
      if (!strcasecmp(f[1], LED_NAMES[i])) {
        ledMode[i] = !strcasecmp(f[2], "blink") ? 2 : (!strcmp(f[2], "1") ? 1 : 0);
        return;
      }
    }
    return;
  }
  if (!strcmp(f[0], "DISP") && fc >= 3) { displayField(f[1], f[2]); return; }
  if (!strcmp(f[0], "BUZZ") && fc >= 2) {
    long ms = strtol(f[1], nullptr, 10);
    if (ms < 50) ms = 50;
    if (ms > 5000) ms = 5000;
    buzzUntilMs = millis() + (unsigned long)ms;
    return;
  }
  // 未知行:忽略(協定第 3 節)
}

static void pollSerial() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\n') {
      rxLine[rxLen] = 0;
      handleHostLine(rxLine);
      rxLen = 0;
    } else if (rxLen < sizeof(rxLine) - 1) {
      rxLine[rxLen++] = c;
    } else {
      rxLen = 0;  // 過長:丟棄
    }
  }
}

// ---- 類比輸入 ----
static float readCentredPot(int pin, float fullScale) {
  int raw = analogRead(pin);
  float v = ((float)raw / (float)ADC_MAX) * 2.0f - 1.0f;   // −1…1
  float value = v * fullScale;
  if (fabsf(value) < POT_DEADBAND_DEG) value = 0.0f;
  if (value > fullScale) value = fullScale;
  if (value < -fullScale) value = -fullScale;
  return value;
}

static int readTelegraphIndex() {
  int raw = analogRead(PIN_TELEGRAPH);
  int step = ADC_MAX / TELEGRAPH_COUNT;
  int idx = raw / step;
  if (idx >= TELEGRAPH_COUNT) idx = TELEGRAPH_COUNT - 1;
  if (telegraphIndex >= 0) {
    // 遲滯:只在離開目前檔位的中心足夠遠時才換檔
    int centre = telegraphIndex * step + step / 2;
    if (abs(raw - centre) < step / 2 + TELEGRAPH_HYSTERESIS) return telegraphIndex;
  }
  return idx;
}

// ---- 輸出 ----
static void updateLeds(unsigned long now) {
  bool blinkOn = ((now / BLINK_HALF_PERIOD_MS) % 2) == 0;
  for (int i = 0; i < LED_COUNT; ++i) {
    bool on = ledMode[i] == 1 || (ledMode[i] == 2 && blinkOn);
    digitalWrite(LED_PINS[i], on ? HIGH : LOW);
  }
  digitalWrite(PIN_BUZZER, (long)(buzzUntilMs - now) > 0 ? HIGH : LOW);
}

void setup() {
  Serial.begin(115200);   // USB CDC:鮑率僅為形式
  analogReadResolution(ADC_BITS);
  analogReadAveraging(ADC_AVERAGING);
  for (int i = 0; i < BUTTON_COUNT; ++i) {
    pinMode(BUTTON_PINS[i], INPUT_PULLUP);
    buttonStable[i] = false;
    buttonLastRaw[i] = false;
    buttonChangedMs[i] = 0;
  }
  pinMode(PIN_NFU_LEFT, INPUT_PULLUP);
  pinMode(PIN_NFU_RIGHT, INPUT_PULLUP);
  for (int i = 0; i < LED_COUNT; ++i) { pinMode(LED_PINS[i], OUTPUT); digitalWrite(LED_PINS[i], LOW); ledMode[i] = 0; }
  pinMode(PIN_BUZZER, OUTPUT);
  digitalWrite(PIN_BUZZER, LOW);
  // 上電自檢:全部 LED 亮 300 ms
  for (int i = 0; i < LED_COUNT; ++i) digitalWrite(LED_PINS[i], HIGH);
  delay(300);
  for (int i = 0; i < LED_COUNT; ++i) digitalWrite(LED_PINS[i], LOW);
  // 等 USB 主機開啟埠(DTR)最多 3 s,之後照常執行(主機稍後連線時 HELLO 會重送)
  unsigned long t0 = millis();
  while (!Serial && millis() - t0 < 3000) delay(10);
  sendHello();
}

void loop() {
  unsigned long now = millis();
  pollSerial();

  // 握手:未收到 ACK 每 2 s 重送 HELLO;USB 重新連線(DTR 落下再起)時重新握手
  static bool wasConnected = false;
  bool connected = (bool)Serial;
  if (connected && !wasConnected) { acked = false; sendHello(); }
  wasConnected = connected;
  if (!acked) {
    if (now - lastHelloMs >= HELLO_RETRY_MS) sendHello();
    updateLeds(now);
    delay(5);
    return;   // 主機未就緒前不送控制行(協定第 3 節 ACK)
  }

  // 心跳 1 Hz
  if (now - lastHeartbeatMs >= HEARTBEAT_MS) { sendLine("HB"); lastHeartbeatMs = now; }

  // 舵輪:變化 ≥ 0.25° 或每 500 ms
  float rud = readCentredPot(PIN_RUDDER, RUDDER_LIMIT_DEG);
  if (fabsf(rud - lastRudderSent) >= RUDDER_SEND_DELTA_DEG || now - lastRudderSentMs >= RUDDER_SEND_PERIOD_MS) {
    sendFloat("RUD", rud);
    lastRudderSent = rud;
    lastRudderSentMs = now;
  }

  // 側推:變化 ≥ 1 % 或每 500 ms
  float thr = readCentredPot(PIN_THRUSTER, 100.0f);
  if (fabsf(thr - lastThrusterSent) >= THRUSTER_SEND_DELTA_PCT || now - lastThrusterSentMs >= THRUSTER_SEND_PERIOD_MS) {
    sendFloat("THR", thr);
    lastThrusterSent = thr;
    lastThrusterSentMs = now;
  }

  // 車鐘檔位:只在變化時送
  int tel = readTelegraphIndex();
  if (tel != telegraphIndex) {
    telegraphIndex = tel;
    char buf[16];
    snprintf(buf, sizeof(buf), "TEL,%s", TELEGRAPH_ORDERS[tel]);
    sendLine(buf);
  }

  // 按鈕:去抖 20 ms,按下送 1、放開送 0
  for (int i = 0; i < BUTTON_COUNT; ++i) {
    bool raw = digitalRead(BUTTON_PINS[i]) == LOW;
    if (raw != buttonLastRaw[i]) { buttonLastRaw[i] = raw; buttonChangedMs[i] = now; }
    if (raw != buttonStable[i] && now - buttonChangedMs[i] >= BUTTON_DEBOUNCE_MS) {
      buttonStable[i] = raw;
      char buf[24];
      snprintf(buf, sizeof(buf), "BTN,%s,%d", BUTTON_NAMES[i], raw ? 1 : 0);
      sendLine(buf);
    }
  }

  // NFU 推桿:L / R / 0,去抖 20 ms
  char nfuRaw = digitalRead(PIN_NFU_LEFT) == LOW ? 'L' : (digitalRead(PIN_NFU_RIGHT) == LOW ? 'R' : '0');
  if (nfuRaw != nfuLastRaw) { nfuLastRaw = nfuRaw; nfuChangedMs = now; }
  if (nfuRaw != nfuState && now - nfuChangedMs >= BUTTON_DEBOUNCE_MS) {
    nfuState = nfuRaw;
    char buf[8];
    snprintf(buf, sizeof(buf), "NFU,%c", nfuState);
    sendLine(buf);
  }

  updateLeds(now);
  delay(5);   // 約 200 Hz 掃描
}
