// dsh-control-compat：兼容旧版手机浏览器（华为自带浏览器等）：补齐新版 Web API。
// 本文件由 DSH 控制台管理：应用启动或「启动服务」时，若网页入口 index.html
// 缺少本补丁，会自动注入；npm 更新覆盖 index.html 后也会自动重新注入。
(function () {
  var c = typeof crypto !== "undefined" ? crypto : null;

  // crypto.randomUUID（非安全上下文不提供）
  if (c && typeof c.randomUUID !== "function") {
    c.randomUUID = function () {
      var b = new Uint8Array(16); c.getRandomValues(b);
      b[6] = (b[6] & 15) | 64; b[8] = (b[8] & 63) | 128;
      var h = ""; for (var i = 0; i < 16; i++) h += ("0" + b[i].toString(16)).slice(-2);
      return h.slice(0, 8) + "-" + h.slice(8, 12) + "-" + h.slice(12, 16) + "-" + h.slice(16, 20) + "-" + h.slice(20);
    };
  }

  // AbortSignal.timeout / AbortSignal.any（Chrome 103+/116+）
  // 同时把超时放大 10 倍（上限 30 分钟）：大会话历史经中继网络传输
  // 可能超过默认 30 秒，放大后避免被误杀。
  if (typeof AbortSignal !== "undefined") {
    var origTimeout = AbortSignal.timeout;
    AbortSignal.timeout = function (ms) {
      var scaled = Math.min((typeof ms === "number" && ms > 0 ? ms : 30000) * 10, 1800000);
      if (typeof origTimeout === "function") return origTimeout.call(AbortSignal, scaled);
      var ctrl = new AbortController();
      var t = setTimeout(function () { ctrl.abort(new DOMException("Timeout", "TimeoutError")); }, scaled);
      if (t && typeof t.unref === "function") t.unref();
      return ctrl.signal;
    };
    if (typeof AbortSignal.any !== "function") {
      AbortSignal.any = function (signals) {
        var ctrl = new AbortController();
        var list = signals ? (Array.isArray(signals) ? signals : Array.prototype.slice.call(signals)) : [];
        function onAbort(ev) {
          if (ctrl.signal.aborted) return;
          var r = (ev && ev.target && ev.target.reason) || new DOMException("Aborted", "AbortError");
          ctrl.abort(r);
        }
        for (var i = 0; i < list.length; i++) {
          var s = list[i];
          if (!s) continue;
          if (s.aborted) { ctrl.abort(s.reason); break; }
          s.addEventListener("abort", onAbort);
        }
        return ctrl.signal;
      };
    }
  }

  // Object.hasOwn（Chrome 93+）
  if (!Object.hasOwn) Object.hasOwn = function (o, k) {
    return Object.prototype.hasOwnProperty.call(o, k);
  };

  // Array/String .at（Chrome 92+）
  if (!Array.prototype.at) Array.prototype.at = function (i) {
    i = Math.trunc(i) || 0; if (i < 0) i += this.length;
    return i >= 0 && i < this.length ? this[i] : void 0;
  };
  if (!String.prototype.at) String.prototype.at = function (i) {
    i = Math.trunc(i) || 0; if (i < 0) i += this.length;
    return i >= 0 && i < this.length ? this.charAt(i) : void 0;
  };

  // String.replaceAll（Chrome 85+）
  if (!String.prototype.replaceAll) String.prototype.replaceAll = function (a, b) {
    if (a instanceof RegExp && !a.global) throw new TypeError("replaceAll requires global regexp");
    return a instanceof RegExp ? this.replace(a, b) : this.split(a).join(b);
  };

  // Promise.any（Chrome 85+）
  if (typeof Promise !== "undefined" && !Promise.any) {
    Promise.any = function (iter) {
      return new Promise(function (resolve, reject) {
        var arr = Array.prototype.slice.call(iter), n = arr.length, errs = [];
        if (!n) { reject(new Error("All promises were rejected")); return; }
        arr.forEach(function (p, i) {
          Promise.resolve(p).then(resolve, function (e) { errs[i] = e; if (--n === 0) reject(errs); });
        });
      });
    };
  }

  // structuredClone（Chrome 98+）
  if (typeof structuredClone !== "function") {
    window.structuredClone = function (v) { return JSON.parse(JSON.stringify(v)); };
  }

  // Array.findLast / findLastIndex（Chrome 97+）
  if (!Array.prototype.findLast) Array.prototype.findLast = function (fn, t) {
    for (var i = this.length - 1; i >= 0; i--) if (fn.call(t, this[i], i, this)) return this[i];
  };
  if (!Array.prototype.findLastIndex) Array.prototype.findLastIndex = function (fn, t) {
    for (var i = this.length - 1; i >= 0; i--) if (fn.call(t, this[i], i, this)) return i;
    return -1;
  };
})();
