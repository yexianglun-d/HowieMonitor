"""使用服务器已安装的宝塔 ACME 客户端管理本项目的单域名证书。"""
import contextlib
import json
import os
from pathlib import Path
import subprocess
import sys

DOMAIN = "monitor.howied.me"
PANEL = Path("/www/server/panel")
WEBROOT = Path("/www/wwwroot") / DOMAIN
CERTIFICATE = PANEL / "vhost/letsencrypt" / DOMAIN / "fullchain.pem"
NGINX = "/www/server/nginx/sbin/nginx"
LOG = Path("/opt/howie-monitor/certificate-renew.log")


def main():
    os.umask(0o077)
    if CERTIFICATE.exists() and subprocess.call(
        ["openssl", "x509", "-in", str(CERTIFICATE), "-checkend", str(30 * 86400), "-noout"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    ) == 0:
        print(json.dumps({"domain": DOMAIN, "renewalRequired": False}))
        return
    if not WEBROOT.is_dir():
        raise RuntimeError("证书验证站点不存在。")
    os.chdir(str(PANEL))
    sys.path[:0] = [str(PANEL), str(PANEL / "class")]
    # ACME 返回值包含私钥，只保留状态；申请过程输出进入本项目私有日志。
    with LOG.open("a") as log, contextlib.redirect_stdout(log), contextlib.redirect_stderr(log):
        import http_requests
        import acme_v2 as panel_acme

        class VerifiedIPv4Transport:
            @staticmethod
            def get(url, *args, **kwargs):
                request = http_requests.http()
                request._ip_type = "ipv4"
                kwargs.pop("s_type", None)
                kwargs.update(verify=True, type="python")
                return request.get(url, *args, **kwargs)

            @staticmethod
            def post(url, *args, **kwargs):
                request = http_requests.http()
                request._ip_type = "ipv4"
                kwargs.pop("s_type", None)
                kwargs.update(verify=True, type="python")
                return request.post(url, *args, **kwargs)

        # 仅修改本次独立进程的传输；宿主未配置 IPv6，宝塔默认传输还会关闭 TLS 校验。
        panel_acme.requests = VerifiedIPv4Transport
        client = panel_acme.acme_v2()
        client._verify = True
        client._request_type = "python"
        # HTTP 验证文件需要供 Nginx 读取；私钥由宝塔保存于私有目录，随后收紧文件权限。
        previous_mask = os.umask(0o022)
        try:
            unfinished = [index for index, order in client._config.get("orders", {}).items()
                          if order.get("domains") == [DOMAIN] and order.get("certificate_url") and order.get("private_key")]
            if unfinished:
                # 已签发但下载中断时恢复同一订单，避免重复申请证书。
                client.get_apis()
                result = client.download_cert(unfinished[-1])
                result["status"] = True
            else:
                result = client.apply_cert([DOMAIN], auth_type="http", auth_to=str(WEBROOT), ca="letsencrypt")
        finally:
            os.umask(previous_mask)
    if not result.get("status"):
        with LOG.open("a") as log:
            log.write("ACME failure: " + str(result.get("msg", "unknown")) + "\n")
        raise RuntimeError("证书申请失败，请检查本项目证书日志。")
    expected = CERTIFICATE.parent.resolve()
    if Path(result["save_path"]).resolve() != expected:
        raise RuntimeError("证书保存目录与预期不一致。")
    if not CERTIFICATE.exists() or not (expected / "privkey.pem").exists():
        raise RuntimeError("证书或私钥未保存。")
    os.chmod(str(expected), 0o700)
    os.chmod(str(expected / "privkey.pem"), 0o600)
    os.chmod(str(CERTIFICATE), 0o644)
    subprocess.check_call([NGINX, "-t"])
    subprocess.check_call([NGINX, "-s", "reload"])
    print(json.dumps({"domain": DOMAIN, "certificateUpdated": True, "expiresAtUnix": result["cert_timeout"]}))


if __name__ == "__main__":
    main()
