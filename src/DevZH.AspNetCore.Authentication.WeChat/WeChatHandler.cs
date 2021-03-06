﻿using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using DevZH.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Http.Authentication;
using Microsoft.AspNetCore.Http.Extensions;
using Newtonsoft.Json.Linq;

namespace DevZH.AspNetCore.Authentication.WeChat
{
    /// <summary>
    ///  微信授权登录处理核心类
    /// </summary>
    public class WeChatHandler : OAuthHandler<WeChatOptions>
    {
        public WeChatHandler(HttpClient backchannel) : base(backchannel)
        {
        }

        /// <summary>
        ///  格式化权限（作用域）
        /// </summary>
        protected override string FormatScope()
        {
            return string.Join(",", Options.Scope);
        }

        /// <summary>
        ///  构建授权链接
        /// </summary>
        protected override string BuildChallengeUrl(AuthenticationProperties properties, string redirectUri)
        {
            var query = new QueryBuilder
            {
                {"appid", Options.AppId},
                {"redirect_uri", redirectUri},
                {"response_type", "code"},
                {"scope", FormatScope()},
                {"state", Options.StateDataFormat.Protect(properties)}
            };
            return Options.AuthorizationEndpoint + query;
        }

        /// <summary>
        ///  获取 相关令牌
        /// </summary>
        /// <param name="code">授权码</param>
        /// <param name="redirectUri">回调地址</param>
        protected override async Task<OAuthTokenResponse> ExchangeCodeAsync(string code, string redirectUri)
        {
            var query = new QueryBuilder
            {
                {"appid", Options.AppId},
                {"secret", Options.AppSecret},
                {"code", code},
                {"grant_type", "authorization_code"},
                {"redirect_uri", redirectUri}
            };
            var response = await Backchannel.GetAsync(Options.TokenEndpoint + query, Context.RequestAborted);
            if (response.IsSuccessStatusCode)
            {
                return OAuthTokenResponse.Success(JObject.Parse(await response.Content.ReadAsStringAsync()));
            }
            return OAuthTokenResponse.Failed(new HttpRequestException($"Failed to get WeChat token ({response.StatusCode}) Please check if the authentication information is correct and the corresponding WeChat API is enabled."));
        }

        /// <summary>
        ///  验证用户，并与本机通信
        /// </summary>
        protected override async Task<AuthenticationTicket> CreateTicketAsync(ClaimsIdentity identity, AuthenticationProperties properties, OAuthTokenResponse tokens)
        {
            var identifier = WeChatHelper.GetId(tokens.Response);
            if (!string.IsNullOrEmpty(identifier))
            {
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, identifier, ClaimValueTypes.String, Options.ClaimsIssuer));
                identity.AddClaim(new Claim("urn:wechat:id", identifier, ClaimValueTypes.String, Options.ClaimsIssuer));
            }
            var query = new QueryBuilder
            {
                {"access_token", tokens.AccessToken},
                {"openid", identifier}
            };
            var response = await Backchannel.GetAsync(Options.UserInformationEndpoint + query, Context.RequestAborted);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Failed to retrived WeChat user information ({response.StatusCode}) Please check if the authentication information is correct and the corresponding WeChat API is enabled.");
            }

            var payload = JObject.Parse(await response.Content.ReadAsStringAsync());

            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), properties, Options.AuthenticationScheme);
            var context = new OAuthCreatingTicketContext(ticket, Context, Options, Backchannel, tokens, payload);

            var name = WeChatHelper.GetName(payload);
            if (!string.IsNullOrEmpty(name))
            {
                identity.AddClaim(new Claim(ClaimTypes.Name, name, ClaimValueTypes.String, Options.ClaimsIssuer));
                identity.AddClaim(new Claim("urn:wechat:name", name, ClaimValueTypes.String, Options.ClaimsIssuer));
            }
            var img = WeChatHelper.GetHeadImage(payload);
            if (!string.IsNullOrEmpty(img))
            {
                identity.AddClaim(new Claim("urn:wechat:headimgurl", img, ClaimValueTypes.String, Options.ClaimsIssuer));
            }

            await Options.Events.CreatingTicket(context);

            return context.Ticket;
        }
    }
}