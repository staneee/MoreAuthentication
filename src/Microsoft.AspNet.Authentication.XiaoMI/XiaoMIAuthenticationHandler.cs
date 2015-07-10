﻿using Microsoft.AspNet.Authentication.OAuth;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNet.Http.Authentication;
using System.Security.Claims;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Net.Http.Headers;
using Microsoft.AspNet.Authentication.Common;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System;

namespace Microsoft.AspNet.Authentication.XiaoMI
{
    /// <summary>
    ///  小米开放平台用户接入授权处理核心类
    /// </summary>
    public class XiaoMIAuthenticationHandler : OAuthAuthenticationHandler<XiaoMIAuthenticationOptions>
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public XiaoMIAuthenticationHandler(HttpClient backchannel) : base(backchannel) { }

        /// <summary>
        ///  创建授权链接
        /// </summary>
        protected override string BuildChallengeUrl(AuthenticationProperties properties, string redirectUri)
        {
            var url = base.BuildChallengeUrl(properties, redirectUri);
            if (Options.SkpConfirm) url += "&skip_confirm=true";
            return url;
        }

        /// <summary>
        /// 获取访问令牌
        /// </summary>
        /// <param name="code">授权码</param>
        /// <param name="redirectUri">回调地址</param>
        protected override async Task<OAuthTokenResponse> ExchangeCodeAsync(string code, string redirectUri)
        {
            var query = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                {"client_id", Options.ClientId},
                {"redirect_uri", redirectUri},
                {"client_secret", Options.ClientSecret},
                {"grant_type", "authorization_code"},
                {"code", code},
                {"token_type", Options.TokenType.ToLower() }
            });
            var message = new HttpRequestMessage(HttpMethod.Get, Options.TokenEndpoint + $"?{await query.ReadAsStringAsync()}");
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await Backchannel.SendAsync(message, Context.RequestAborted);
            response.EnsureSuccessStatusCode();
            // Replace("&&&START&&&", "")
            return new OAuthTokenResponse(JObject.Parse((await response.Content.ReadAsStringAsync()).Remove(0,11)));
        }

        /// <summary>
        ///  获取用户参数，与本机通信
        /// </summary>
        protected override async Task<AuthenticationTicket> CreateTicketAsync(ClaimsIdentity identity, AuthenticationProperties properties, OAuthTokenResponse tokens)
        {
            var openId = tokens.Response.Value<string>("openId");
            if (!string.IsNullOrEmpty(openId))
            {
                identity.AddClaim(new Claim("urn:mi:openid", openId, ClaimValueTypes.String, Options.ClaimsIssuer));
            }
            var type = tokens.TokenType.ToEnum<TokenType>();
            var content = await new FormUrlEncodedContent(new Dictionary<string, string>
            {
                {"clientId", Options.ClientId },
                {"token", tokens.AccessToken }
            }).ReadAsStringAsync();
            var message = new HttpRequestMessage(HttpMethod.Get, $"{Options.UserInformationEndpoint}?{content}");
            if (type == TokenType.MAC)
            {
                var key = tokens.Response.Value<string>("mac_key");
                var algorithm = tokens.Response.Value<string>("mac_algorithm");
                message.Headers.Authorization = new AuthenticationHeaderValue("MAC", ComputeMAC(tokens.AccessToken, key, algorithm, message));
            }
            var response = await Backchannel.SendAsync(message, Context.RequestAborted);
            response.EnsureSuccessStatusCode();
            var payload = JObject.Parse(await response.Content.ReadAsStringAsync());

            var notification = new OAuthAuthenticatedContext(Context, Options, Backchannel, tokens, payload)
            {
                Properties = properties,
                Principal = new ClaimsPrincipal(identity)
            };

            var identifier = XiaoMIAuthenticationHelper.GetId(payload);
            if (!string.IsNullOrEmpty(identifier))
            {
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, identifier, ClaimValueTypes.String, Options.ClaimsIssuer));
                identity.AddClaim(new Claim("urn:mi:id", identifier, ClaimValueTypes.String, Options.ClaimsIssuer));
            }

            var name = XiaoMIAuthenticationHelper.GetNickName(payload);
            if (!string.IsNullOrEmpty(name))
            {
                identity.AddClaim(new Claim(ClaimTypes.Name, name, ClaimValueTypes.String, Options.ClaimsIssuer));
                identity.AddClaim(new Claim("urn:mi:nickname", name, ClaimValueTypes.String, Options.ClaimsIssuer));
            }
            var icon = XiaoMIAuthenticationHelper.GetIcon(payload);
            if (!string.IsNullOrEmpty(icon))
            {
                identity.AddClaim(new Claim("urn:mi:icon", icon, ClaimValueTypes.String, Options.ClaimsIssuer));
            }

            await Options.Notifications.Authenticated(notification);

            return new AuthenticationTicket(notification.Principal, notification.Properties, notification.Options.AuthenticationScheme);
        }

        /// <summary>
        ///  计算 MAC 模式值
        /// </summary>
        /// <param name="token">访问令牌</param>
        /// <param name="key">加密密钥</param>
        /// <exception cref="NullReferenceException">message 为空</exception>
        private string ComputeMAC(string token, string key, string algorithm = "HmacSHA1" , HttpRequestMessage message = null)
        {
            var nonce = ComputeNonce();
            var param = new[] {
                nonce,
                message.Method.Method,
                // 我去，耗了我一上午，我还以为 message.Headers.Host 取得到值呢
                message.RequestUri.Host,
                message.RequestUri.LocalPath,
                message.RequestUri.Query.Substring(1)
            };
            var mac = ComputeSignature(string.Join("\n", param) + "\n", key, algorithm);
            return $"access_token=\"{token.ToEscapeData()}\", nonce=\"{nonce.ToEscapeData()}\",mac=\"{mac.ToEscapeData()}\"";
        }

        /// <summary>
        ///  计算 nonce 值
        /// </summary>
        private string ComputeNonce()
        {
            var pre = (new Random()).Next();
            var utc = (DateTime.UtcNow - Epoch).TotalMinutes.ToString("F0"); // 懒得用 Convert.ToInt64().ToString()
            return $"{pre}:{utc}";
        }

        /// <summary>
        ///  加密
        /// </summary>
        private string ComputeSignature(string input, string mac_key, string algorithm)
        {
            // MAC 草案上一般使用 hmac-sha-1 和 hmac-sha-256 来指代的
            // 小米开放平台是用 Java 上的 算法名称，现只支持 HmacSHA1 算法
            switch (algorithm.Replace("-","").ToLower())
            {
                case "hmacsha1":
                    return HmacSHA1Compute(input, mac_key);
                case "hmacsha256":
                    return HmacSHA256Compute(input, mac_key);
                default:
                    throw new NotSupportedException("未支持的算法类型");
            }
        }

        private string HmacSHA1Compute(string input, string mac_key)
        {
            using (var algorithm = new HMACSHA1(Encoding.UTF8.GetBytes(mac_key)))
            {
                var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(input));
                return Convert.ToBase64String(hash);
            }
        }

        private string HmacSHA256Compute(string input, string mac_key)
        {
            using(var algorithm = new HMACSHA256(Encoding.UTF8.GetBytes(mac_key)))
            {
                var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(input));
                return Convert.ToBase64String(hash);
            }
        }
    }
}