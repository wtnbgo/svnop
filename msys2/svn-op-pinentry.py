#!/usr/bin/env python3
"""svn-op-pinentry: Subversion 用に 1Password を引く pinentry

gpg-agent.conf の pinentry-program に指定して使う。

- svn の GPG-Agent プロバイダからの問い合わせ (SETKEYINFO u/<md5(realm)> と
  "... password for <realm>" の SETDESC) なら、realm とユーザ名で 1Password を引いて返す。
- 見つからないとき / 前回の値で認証失敗した (SETERROR 付き) ときは、
  fallback の pinentry でユーザに入力させ、確認のうえ 1Password に記録する。
- svn 以外の問い合わせ (gpg の鍵パスフレーズ等) は fallback pinentry にそのまま中継する。

設定は ~/.config/svn-op/config.json (無ければ既定値)、環境変数 SVNOP_* で上書きできる。
pinentry は gpg-agent の環境で起動されるので、環境変数より設定ファイルを推奨。
"""
import hashlib
import json
import os
import re
import subprocess
import sys
import time

CONFIG_DIR = os.path.expanduser("~/.config/svn-op")
DEFAULTS = {
    "backend": "op",                       # op | file (検証用モック)
    "svnop": "",                           # svnop.exe のパス。agent が動いていればそれ経由で 1Password を引く
    "op": "op.exe",                        # 1Password CLI
    "vault": "",                           # 空なら op の既定
    "tag": "svn",                          # 対象アイテムに付けるタグ
    "realm_field": "svn_realm",            # realm を入れるフィールドのラベル
    "mock_file": os.path.join(CONFIG_DIR, "mock-items.json"),
    "fallback": "/usr/bin/pinentry-w32.exe",
    "confirm_save": True,                  # 1Password へ記録する前に確認する
    "log": "",                             # デバッグログ (パスワードは書かない)
}


def load_config():
    cfg = dict(DEFAULTS)
    try:
        with open(os.path.join(CONFIG_DIR, "config.json"), encoding="utf-8") as f:
            cfg.update(json.load(f))
    except FileNotFoundError:
        pass
    for k in DEFAULTS:
        v = os.environ.get("SVNOP_" + k.upper())
        if v is not None:
            cfg[k] = v if not isinstance(DEFAULTS[k], bool) else v not in ("0", "false", "no", "")
    return cfg


CFG = load_config()


def _is_ja():
    lang = os.environ.get("SVNOP_LANG") or os.environ.get("LC_ALL") or os.environ.get("LC_MESSAGES") or os.environ.get("LANG") or ""
    return lang.lower().startswith("ja")


JA = _is_ja()


def T(ja, en):
    """表示言語の切り替え (SVNOP_LANG が優先、無ければ LANG 等)"""
    return ja if JA else en


def log(msg):
    if CFG["log"]:
        # 改行などの制御文字はエスケープする (realm やユーザ名でログの行を偽装されないように)
        safe = "".join(c if c.isprintable() else "\\x%02x" % ord(c) for c in msg)
        with open(CFG["log"], "a", encoding="utf-8") as f:
            f.write(time.strftime("%H:%M:%S ") + safe + "\n")


# ---- Assuan のエスケープ ----

def unescape(s):
    """Assuan の %XX をバイトとして復号してから UTF-8 として解釈する (不正な UTF-8 は ValueError)"""
    raw = s.encode("utf-8")
    out = bytearray()
    i = 0
    while i < len(raw):
        if raw[i] == 0x25 and i + 2 < len(raw) and re.fullmatch(rb"[0-9A-Fa-f]{2}", raw[i + 1:i + 3]):
            out.append(int(raw[i + 1:i + 3], 16))
            i += 3
        else:
            out.append(raw[i])
            i += 1
    return out.decode("utf-8")


def escape_data(s):
    return s.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


# ---- 1Password バックエンド ----

class OpBackend:
    """op CLI 経由。realm → item id の対応はキャッシュファイルに持つ (秘密情報ではない)"""

    def __init__(self, cfg):
        self.cfg = cfg
        self.map_path = os.path.join(CONFIG_DIR, "realm-map.json")

    def _op(self, args, stdin=None):
        # 探索パス上の偽の op.exe を使わないよう、絶対パスで指定されたものだけを使う (svnop setup msys2 が署名を確かめて書く)
        if not os.path.isabs(self.cfg["op"]):
            raise RuntimeError("op must be an absolute path (set by svnop setup msys2)")
        cmd = [self.cfg["op"]] + args
        if self.cfg["vault"]:
            cmd += ["--vault", self.cfg["vault"]]
        log("op " + " ".join(a if "=" not in a else a.split("=")[0] + "=***" for a in args))
        r = subprocess.run(cmd, input=stdin, capture_output=True, text=True, encoding="utf-8")
        if r.returncode != 0:
            raise RuntimeError("op failed: " + r.stderr.strip())
        return r.stdout

    def _load_map(self):
        try:
            with open(self.map_path, encoding="utf-8") as f:
                return json.load(f)
        except (FileNotFoundError, ValueError):
            return {}

    def _save_map(self, m):
        os.makedirs(CONFIG_DIR, exist_ok=True)
        with open(self.map_path, "w", encoding="utf-8") as f:
            json.dump(m, f, ensure_ascii=False, indent=1)

    # op を呼ぶたびに 1Password の承認が要る (Windows では呼び出し元プロセス単位で、
    # msys2 からだと毎回別プロセス扱い) ので、1 回の問い合わせで呼ぶ回数を最小にする。
    # - 検索は item id (対応表) かタイトルで item get 1 回
    # - 更新は検索で取った JSON を使い回して item edit 1 回

    def _get(self, ref):
        try:
            return json.loads(self._op(["item", "get", ref, "--reveal", "--format", "json"]))
        except RuntimeError as e:
            log(str(e))
            return None

    def _key(self, realm, user):
        return realm + "\n" + user

    @staticmethod
    def title(realm, user):
        return "SVN %s @ %s" % (user, realm)

    def find(self, realm, user):
        self.last = None
        m = self._load_map()
        ref = m.get(self._key(realm, user)) or self.title(realm, user)
        it = self._get(ref)
        # id 指定の item get はアーカイブ済みも返すので除外する
        if not it or it.get("state") == "ARCHIVED":
            return None, None
        f = {x.get("label"): x.get("value") for x in it.get("fields", [])}
        if f.get(self.cfg["realm_field"]) != realm or f.get("username") != user:
            log("item mismatch: " + it["id"])
            return None, None
        if m.get(self._key(realm, user)) != it["id"]:
            m[self._key(realm, user)] = it["id"]
            self._save_map(m)
        self.last = it
        return it["id"], f.get("password")

    def save(self, item_id, realm, user, password):
        if item_id:
            # 値をコマンドラインに出さないよう、JSON を書き換えて stdin で渡す
            it = self.last if self.last and self.last["id"] == item_id else self._get(item_id)
            for f in it.get("fields", []):
                if f.get("purpose") == "PASSWORD" or f.get("id") == "password":
                    f["value"] = password
            self._op(["item", "edit", item_id, "--format", "json"], stdin=json.dumps(it))
            return item_id
        tmpl = {
            "title": self.title(realm, user),
            "category": "LOGIN",
            "tags": [self.cfg["tag"]],
            "fields": [
                {"id": "username", "type": "STRING", "purpose": "USERNAME", "label": "username", "value": user},
                {"id": "password", "type": "CONCEALED", "purpose": "PASSWORD", "label": "password", "value": password},
                {"id": "svn_realm", "type": "STRING", "label": self.cfg["realm_field"], "value": realm},
            ],
        }
        it = json.loads(self._op(["item", "create", "--format", "json", "-"], stdin=json.dumps(tmpl)))
        m = self._load_map()
        m[self._key(realm, user)] = it["id"]
        self._save_map(m)
        return it["id"]


class AgentBackend:
    """常駐している svnop agent 経由。op を呼ぶのは agent なので、承認は agent 側で 10 分に 1 回程度で済む。
    cygwin から Windows の名前付きパイプは開けないので、svnop.exe call に中継させる"""

    NOT_RUNNING = 3

    def __init__(self, cfg):
        self.svnop = cfg["svnop"]

    def _call(self, req):
        r = subprocess.run([self.svnop, "call"], input=json.dumps(req) + "\n",
                           capture_output=True, text=True, encoding="utf-8")
        if r.returncode == self.NOT_RUNNING:
            raise ConnectionError("agent is not running")
        if r.returncode != 0:
            raise RuntimeError("svnop call failed: " + r.stderr.strip())
        res = json.loads(r.stdout)
        if "error" in res:
            raise RuntimeError("agent: " + res["error"])
        return res

    def available(self):
        try:
            self._call({"cmd": "ping"})
            return True
        except (ConnectionError, RuntimeError, OSError) as e:
            log("agent unavailable: %s" % e)
            return False

    def find(self, realm, user):
        res = self._call({"cmd": "find", "realm": realm, "user": user})
        return (res["id"], res.get("password")) if res.get("found") else (None, None)

    def save(self, item_id, realm, user, password):
        return self._call({"cmd": "save", "realm": realm, "user": user, "password": password})["id"]


def make_backend():
    if CFG["backend"] == "file":
        return FileBackend(CFG)
    if CFG["svnop"]:
        agent = AgentBackend(CFG)
        if agent.available():
            return agent
    return OpBackend(CFG)


class FileBackend:
    """検証用モック: JSON ファイルに平文で持つ"""

    def __init__(self, cfg):
        self.path = cfg["mock_file"]

    def _load(self):
        try:
            with open(self.path, encoding="utf-8") as f:
                return json.load(f)
        except (FileNotFoundError, ValueError):
            return []

    def find(self, realm, user):
        for i, it in enumerate(self._load()):
            if it["realm"] == realm and it["username"] == user:
                return str(i), it["password"]
        return None, None

    def save(self, item_id, realm, user, password):
        items = self._load()
        if item_id is not None:
            items[int(item_id)]["password"] = password
        else:
            items.append({"realm": realm, "username": user, "password": password})
            item_id = str(len(items) - 1)
        with open(self.path, "w", encoding="utf-8") as f:
            json.dump(items, f, ensure_ascii=False, indent=1)
        return item_id


# ---- fallback pinentry ----

class Fallback:
    def __init__(self, path):
        self.p = subprocess.Popen([path], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  text=True, encoding="utf-8", bufsize=1)
        self.readline()  # greeting

    def readline(self):
        return self.p.stdout.readline().rstrip("\n")

    def send(self, line):
        self.p.stdin.write(line + "\n")
        self.p.stdin.flush()

    def transact(self, line, emit=None, inquire=None):
        """1 コマンド送って、OK/ERR までの応答行を返す。
        emit を渡すと応答行をその場で渡す (中継用)。
        fallback が INQUIRE で問い合わせてきたら inquire(行) に任せ、無ければ CAN で断る"""
        self.send(line)
        out = []
        while True:
            r = self.readline()
            if r.startswith("INQUIRE"):
                if emit:
                    emit(r)
                if inquire:
                    inquire(r)
                else:
                    self.send("CAN")
                continue
            if emit:
                emit(r)
            else:
                out.append(r)
            if r.startswith(("OK", "ERR")) or r == "":
                return out

    def close(self):
        try:
            self.transact("BYE")
        except (BrokenPipeError, OSError):
            pass
        self.p.wait(timeout=5)


# ---- main loop ----

DESC_RE = re.compile(r"(<[a-z+]+://[^>]+>.*)$", re.S)
PROMPT_RE = re.compile(r"'(.*)'")
KEYINFO_RE = re.compile(r"^u/([0-9a-f]{32})$")


def reply(line):
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def svn_request(st):
    """svn からの問い合わせなら (realm, user) を返す"""
    m = KEYINFO_RE.match(st.get("SETKEYINFO", ""))
    d = DESC_RE.search(st.get("SETDESC", ""))
    u = PROMPT_RE.search(st.get("SETPROMPT", ""))
    if not (m and d and u):
        return None
    realm = d.group(1).strip()
    # cache_id = md5(realm) で realm の取り出しが正しいことを確かめる (説明文が翻訳されていても判別できる)
    if hashlib.md5(realm.encode("utf-8")).hexdigest() != m.group(1):
        log("realm/keyinfo mismatch: " + realm)
        return None
    return realm, u.group(1)


def ask_user(history, st, error=None):
    fb = Fallback(CFG["fallback"])
    try:
        for h in history:
            fb.transact(h)
        if error:
            fb.transact("SETERROR " + error)
        res = fb.transact("GETPIN")
        ok = res[-1].startswith("OK")
        # データは複数の D 行に分かれることがあるので、つなげてから復号する
        data = "".join(r[2:] for r in res if r.startswith("D "))
        try:
            pw = unescape(data) if ok and data else None
        except ValueError:
            log("fallback returned invalid UTF-8; treated as cancelled")
            pw = None
        return pw, fb
    except Exception:
        fb.close()
        raise


def confirm(fb, realm, user):
    fb.transact("SETERROR")  # 前のエラー表示を消す
    fb.transact("SETDESC " + escape_data(T("この SVN パスワードを 1Password に記録しますか?\n%s\nユーザー: %s",
                                           "Record this SVN password in 1Password?\n%s\nUser: %s") % (realm, user)))
    res = fb.transact("CONFIRM")
    return res[-1].startswith("OK")


def handle_getpin(history, st):
    req = svn_request(st)
    if not req:
        return None
    realm, user = req
    backend = make_backend()
    log("backend: " + type(backend).__name__)
    retry = bool(st.get("SETERROR"))
    item_id, pw = None, None
    try:
        item_id, pw = backend.find(realm, user)
    except Exception as e:
        log("find failed: %s" % e)
    log("svn realm=%s user=%s found=%s retry=%s" % (realm, user, pw is not None, retry))
    if pw is not None and not retry:
        return ["D " + escape_data(pw), "OK"]

    # 1Password に無い / 1Password の値で認証失敗 → ユーザに聞いて記録
    if retry and pw is not None:
        err = T("1Password の値で認証に失敗しました。新しいパスワードを入力してください",
                "Authentication failed with the value in 1Password. Enter the new password")
    else:
        err = st.get("SETERROR")
    newpw, fb = ask_user(history, st, escape_data(err) if err else None)
    try:
        if newpw is None:
            return ["ERR 83886179 Operation cancelled <Pinentry>"]
        # agent 経由のときは agent が記録の確認を出すので、ここでは聞かない
        need_confirm = CFG["confirm_save"] and not isinstance(backend, AgentBackend)
        if (not need_confirm) or confirm(fb, realm, user):
            try:
                backend.save(item_id, realm, user, newpw)
                log("saved realm=%s user=%s" % (realm, user))
            except Exception as e:
                log("save failed: %s" % e)
    finally:
        fb.close()
    return ["D " + escape_data(newpw), "OK"]


def relay_inquire(fb):
    """fallback の INQUIRE への gpg-agent の答え (D 行 ... END / CAN) をそのまま fallback に渡す"""
    while True:
        line = sys.stdin.readline()
        if line == "":
            fb.send("CAN")
            return
        line = line.rstrip("\n")
        fb.send(line)
        if line == "END" or line.startswith("CAN"):
            return


def main():
    reply("OK Pleased to meet you, svn-op-pinentry")
    history = []   # fallback に再生する OPTION / SET* 行
    st = {}
    fb = None      # svn 以外と分かったら以降は中継
    for raw in sys.stdin:
        line = raw.rstrip("\n")
        cmd, _, arg = line.partition(" ")
        if fb:
            fb.transact(line, emit=reply, inquire=lambda _: relay_inquire(fb))
            if cmd == "BYE":
                fb.p.wait(timeout=5)
                return
            continue
        if cmd == "BYE":
            reply("OK closing connection")
            return
        if cmd == "CLEARPASSPHRASE":
            # gpg-agent のキャッシュ消去の通知。外部キャッシュは持たないので何もしない
            reply("OK")
            continue
        if cmd == "GETINFO":
            if arg == "pid":
                reply("D %d" % os.getpid())
            elif arg == "flavor":
                reply("D svn-op")
            elif arg == "version":
                reply("D 0.1")
            reply("OK")
            continue
        if cmd in ("OPTION", "SETKEYINFO", "SETDESC", "SETPROMPT", "SETERROR", "SETTITLE",
                   "SETOK", "SETCANCEL", "SETNOTOK", "SETTIMEOUT", "SETQUALITYBAR",
                   "SETQUALITYBAR_TT", "SETREPEAT", "SETREPEATERROR", "SETGENPIN", "SETGENPIN_TT"):
            if cmd != "OPTION":
                try:
                    st[cmd] = unescape(arg)
                except ValueError:
                    st[cmd] = arg  # 不正な符号化: svn の問い合わせとは判定されない
            history.append(line)
            reply("OK")
            continue
        if cmd == "GETPIN":
            out = handle_getpin([h for h in history if not h.startswith("SETERROR")], st)
            if out is not None:
                for r in out:
                    reply(r)
                st.pop("SETERROR", None)
                continue
        # svn 以外: fallback を起こしてここまでの設定を再生し、以降は中継
        log("pass through: " + cmd)
        fb = Fallback(CFG["fallback"])
        for h in history:
            fb.transact(h)
        fb.transact(line, emit=reply, inquire=lambda _: relay_inquire(fb))


if __name__ == "__main__":
    main()
