﻿//#define REFRESH_TOKENS

using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Xml.Serialization;
using ISBoxerEVELauncher.Utility;
using ISBoxerEVELauncher.Extensions;

namespace ISBoxerEVELauncher
{
    public enum DirectXVersion
    {
        Default,
        dx9,
        dx11,
    }

    /// <summary>
    /// An EVE Online account and related data
    /// </summary>
    public class EVEAccount : INotifyPropertyChanged, IDisposable, ISBoxerEVELauncher.Launchers.ILaunchTarget
    {

        [XmlIgnore]
        private Guid challengeCodeSource;
        [XmlIgnore]
        private byte[] challengeCode;
        [XmlIgnore]
        private string challengeHash;
        [XmlIgnore]
        private Guid state;
        [XmlIgnore]
        private string authCode;


        public EVEAccount()
        {
            state = Guid.NewGuid();
            challengeCodeSource = Guid.NewGuid();
            challengeCode = Encoding.UTF8.GetBytes(challengeCodeSource.ToString().Replace("-", ""));
            challengeHash = Base64UrlEncoder.Encode(Crypto.GenerateSHA256Hash(Base64UrlEncoder.Encode(challengeCode)));
        }



        /// <summary>
        /// An Outh2 Access Token
        /// </summary>
        public class Token
        {
            public Token()
            {

            }

            /// <summary>
            /// We usually just need to parse a Uri for the Access Token details. So here is the constructor that does it for us.
            /// </summary>
            /// <param name="fromUri"></param>
            public Token(authObj resp)
            {

                TokenString = resp.access_token;
                Expiration = DateTime.Now.AddMinutes(resp.expires_in);
            }

            public override string ToString()
            {
                return TokenString;
            }

            /// <summary>
            /// Determine if the Access Token is expired. If it is, we know we can't use it...
            /// </summary>
            public bool IsExpired
            {
                get
                {
                    return DateTime.Now >= Expiration;
                }
            }

            /// <summary>
            /// The actual token data
            /// </summary>
            public string TokenString { get; set; }
            /// <summary>
            /// When the token is good until...
            /// </summary>
            public DateTime Expiration { get; set; }
        }

        CookieContainer _Cookies;

        /// <summary>
        /// The EVE login process requires cookies; this will ensure we maintain the same cookies for the account
        /// </summary>
        [XmlIgnore]
        CookieContainer Cookies
        {
            get
            {
                if (_Cookies == null)
                {
                    if (!string.IsNullOrEmpty(NewCookieStorage))
                    {
                        BinaryFormatter formatter = new BinaryFormatter();


                        using (Stream s = new MemoryStream(Convert.FromBase64String(NewCookieStorage)))
                        {
                            _Cookies = (CookieContainer)formatter.Deserialize(s);
                        }
                    }
                    else
                        _Cookies = new CookieContainer();
                }
                return _Cookies;
            }
            set
            {
                _Cookies = value;
            }
        }

        public void UpdateCookieStorage()
        {
            if (Cookies == null)
            {
                NewCookieStorage = null;
                return;
            }

            using (MemoryStream ms = new MemoryStream())
            {
                BinaryFormatter formatter = new BinaryFormatter();
                formatter.Serialize(ms, Cookies);
                ms.Flush();
                ms.Seek(0, SeekOrigin.Begin);

                NewCookieStorage = Convert.ToBase64String(ms.ToArray());
            }

        }

        string _Username;
        /// <summary>
        /// EVE Account username
        /// </summary>
        public string Username { get { return _Username; } set { _Username = value; OnPropertyChanged("Username"); } }

        /// <summary>
        /// Old cookie storage. If found in the XML, it will automatically be split into separate storage
        /// </summary>
        public string CookieStorage
        {
            get
            {
                return null;// return ISBoxerEVELauncher.CookieStorage.GetCookies(this);
            }
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    ISBoxerEVELauncher.CookieStorage.SetCookies(this, value);
                    EVEAccount.ShouldUgradeCookieStorage = true;
                }
            }
        }

        public static bool ShouldUgradeCookieStorage { get; private set; }
        /// <summary>
        /// New method of storing cookies
        /// </summary>
        [XmlIgnore]
        public string NewCookieStorage
        {
            get
            {
                return ISBoxerEVELauncher.CookieStorage.GetCookies(this);
            }
            set
            {
                ISBoxerEVELauncher.CookieStorage.SetCookies(this, value);
            }
        }


        #region Password
        System.Security.SecureString _SecurePassword;
        /// <summary>
        /// A Secure (and non-plaintext) representation of the password. This will NOT be stored in XML.
        /// </summary>
        [XmlIgnore]
        public System.Security.SecureString SecurePassword { get { return _SecurePassword; } set { _SecurePassword = value; OnPropertyChanged("SecurePassword"); EncryptedPassword = null; EncryptedPasswordIV = null; } }

        string _EncryptedPassword;
        /// <summary>
        /// An encrypted version of the password for the account. It is protected by the Password Master Key. Changing the Password Master Key will wipe this.
        /// </summary>
        public string EncryptedPassword
        {
            get
            {
                return _EncryptedPassword;
            }
            set { _EncryptedPassword = value; OnPropertyChanged("EncryptedPassword"); }
        }

        string _EncryptedPasswordIV;
        /// <summary>
        /// The Initialization Vector used to encrypt the password
        /// </summary>
        public string EncryptedPasswordIV { get { return _EncryptedPasswordIV; } set { _EncryptedPasswordIV = value; OnPropertyChanged("EncryptedPasswordIV"); } }

        /// <summary>
        /// Sets the encrypted password to the given SecureString, if possible
        /// </summary>
        /// <param name="password"></param>
        void SetEncryptedPassword(System.Security.SecureString password)
        {
            if (!App.Settings.UseMasterKey || password == null)
            {
                ClearEncryptedPassword();
                return;
            }

            if (!App.Settings.RequestMasterPassword())
            {
                System.Windows.MessageBox.Show("Your configured Master Password is required in order to save EVE Account passwords. It can be reset or disabled by un-checking 'Save passwords (securely)', and then all currently saved EVE Account passwords will be lost.");
                return;
            }

            using (RijndaelManaged rjm = new RijndaelManaged())
            {
                if (string.IsNullOrEmpty(EncryptedPasswordIV))
                {
                    rjm.GenerateIV();
                    EncryptedPasswordIV = Convert.ToBase64String(rjm.IV);
                }
                else
                    rjm.IV = Convert.FromBase64String(EncryptedPasswordIV);

                using (SecureBytesWrapper sbwKey = new SecureBytesWrapper(App.Settings.PasswordMasterKey, true))
                {
                    rjm.Key = sbwKey.Bytes;

                    using (ICryptoTransform encryptor = rjm.CreateEncryptor())
                    {
                        using (SecureStringWrapper ssw2 = new SecureStringWrapper(password, Encoding.Unicode))
                        {
                            byte[] inblock = ssw2.ToByteArray();
                            byte[] encrypted = encryptor.TransformFinalBlock(inblock, 0, inblock.Length);
                            EncryptedPassword = Convert.ToBase64String(encrypted);
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Attempts to prepare the encrypted verison of the currently active SecurePassword
        /// </summary>
        public void EncryptPassword()
        {
            SetEncryptedPassword(SecurePassword);
        }

        /// <summary>
        /// Prepares the EVEAccount for storage by ensuring that the Encrypted fields are set, if available
        /// </summary>
        public void PrepareStorage()
        {
            if (SecurePassword != null)
            {
                EncryptPassword();
            }
            if (SecureCharacterName != null)
            {
                EncryptCharacterName();
            }
        }

        /// <summary>
        /// Removes the encrypted password and IV
        /// </summary>
        public void ClearEncryptedPassword()
        {
            EncryptedPassword = null;
            EncryptedPasswordIV = null;
        }

        /// <summary>
        /// Decrypts the currently EncryptedPassword if possible, populating SecurePassword (which can then be used to log in...)
        /// </summary>
        public void DecryptPassword(bool allowPopup)
        {
            if (string.IsNullOrEmpty(EncryptedPassword) || string.IsNullOrEmpty(EncryptedPasswordIV))
            {
                // no password stored to decrypt.
                return;
            }
            // password is indeed encrypted

            if (!App.Settings.HasPasswordMasterKey)
            {
                // Master Password not yet entered
                if (!allowPopup)
                {
                    // can't ask for it right now
                    return;
                }

                // ok, ask for it
                if (!App.Settings.RequestMasterPassword())
                {
                    // not entered. can't decrypt.
                    return;
                }
            }
            using (RijndaelManaged rjm = new RijndaelManaged())
            {
                rjm.IV = Convert.FromBase64String(EncryptedPasswordIV);

                using (SecureBytesWrapper sbwKey = new SecureBytesWrapper(App.Settings.PasswordMasterKey, true))
                {
                    rjm.Key = sbwKey.Bytes;
                    using (ICryptoTransform decryptor = rjm.CreateDecryptor())
                    {
                        byte[] pass = Convert.FromBase64String(EncryptedPassword);

                        using (SecureBytesWrapper sbw = new SecureBytesWrapper())
                        {
                            sbw.Bytes = decryptor.TransformFinalBlock(pass, 0, pass.Length);

                            SecurePassword = new System.Security.SecureString();
                            foreach (char c in Encoding.Unicode.GetChars(sbw.Bytes))
                            {
                                SecurePassword.AppendChar(c);
                            }
                            SecurePassword.MakeReadOnly();
                        }
                    }
                }
            }
        }
        #endregion

        #region CharacterName
        System.Security.SecureString _SecureCharacterName;
        /// <summary>
        /// A Secure (and non-plaintext) representation of the CharacterName. This will NOT be stored in XML.
        /// </summary>
        [XmlIgnore]
        public System.Security.SecureString SecureCharacterName { get { return _SecureCharacterName; } set { _SecureCharacterName = value; OnPropertyChanged("SecureCharacterName"); EncryptedCharacterName = null; EncryptedCharacterNameIV = null; } }

        string _EncryptedCharacterName;
        /// <summary>
        /// An encrypted version of the CharacterName for the account. It is protected by the CharacterName Master Key. Changing the CharacterName Master Key will wipe this.
        /// </summary>
        public string EncryptedCharacterName
        {
            get
            {
                return _EncryptedCharacterName;
            }
            set { _EncryptedCharacterName = value; OnPropertyChanged("EncryptedCharacterName"); }
        }

        string _EncryptedCharacterNameIV;
        /// <summary>
        /// The Initialization Vector used to encrypt the CharacterName
        /// </summary>
        public string EncryptedCharacterNameIV { get { return _EncryptedCharacterNameIV; } set { _EncryptedCharacterNameIV = value; OnPropertyChanged("EncryptedCharacterNameIV"); } }

        /// <summary>
        /// Attempts to prepare the encrypted verison of the currently active SecureCharacterName
        /// </summary>
        public void EncryptCharacterName()
        {
            SetEncryptedCharacterName(SecureCharacterName);
        }

        /// <summary>
        /// Removes the encrypted CharacterName and IV
        /// </summary>
        public void ClearEncryptedCharacterName()
        {
            EncryptedCharacterName = null;
            EncryptedCharacterNameIV = null;
        }

        /// <summary>
        /// Sets the encrypted CharacterName to the given SecureString, if possible
        /// </summary>
        /// <param name="CharacterName"></param>
        void SetEncryptedCharacterName(System.Security.SecureString CharacterName)
        {
            if (!App.Settings.UseMasterKey || CharacterName == null)
            {
                ClearEncryptedCharacterName();
                return;
            }

            if (!App.Settings.RequestMasterPassword())
            {
                System.Windows.MessageBox.Show("Your configured Master Password is required in order to save EVE Account Character Names and passwords. It can be reset or disabled by un-checking 'Save passwords (securely)', and then all currently saved EVE Account Character Names will be lost.");
                return;
            }

            using (RijndaelManaged rjm = new RijndaelManaged())
            {
                if (string.IsNullOrEmpty(EncryptedCharacterNameIV))
                {
                    rjm.GenerateIV();
                    EncryptedCharacterNameIV = Convert.ToBase64String(rjm.IV);
                }
                else
                    rjm.IV = Convert.FromBase64String(EncryptedCharacterNameIV);

                using (SecureBytesWrapper sbwKey = new SecureBytesWrapper(App.Settings.PasswordMasterKey, true))
                {
                    rjm.Key = sbwKey.Bytes;

                    using (ICryptoTransform encryptor = rjm.CreateEncryptor())
                    {
                        using (SecureStringWrapper ssw2 = new SecureStringWrapper(CharacterName, Encoding.Unicode))
                        {
                            byte[] inblock = ssw2.ToByteArray();
                            byte[] encrypted = encryptor.TransformFinalBlock(inblock, 0, inblock.Length);
                            EncryptedCharacterName = Convert.ToBase64String(encrypted);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Decrypts the currently EncryptedCharacterName if possible, populating SecureCharacterName (which can then be used to log in...)
        /// </summary>
        public void DecryptCharacterName(bool allowPopup)
        {
            if (string.IsNullOrEmpty(EncryptedCharacterName) || string.IsNullOrEmpty(EncryptedCharacterNameIV))
            {
                // no CharacterName stored to decrypt.
                return;
            }
            // CharacterName is indeed encrypted

            if (!App.Settings.HasPasswordMasterKey)
            {
                // Master CharacterName not yet entered
                if (!allowPopup)
                {
                    // can't ask for it right now
                    return;
                }

                // ok, ask for it
                if (!App.Settings.RequestMasterPassword())
                {
                    // not entered. can't decrypt.
                    return;
                }
            }
            using (RijndaelManaged rjm = new RijndaelManaged())
            {
                rjm.IV = Convert.FromBase64String(EncryptedCharacterNameIV);

                using (SecureBytesWrapper sbwKey = new SecureBytesWrapper(App.Settings.PasswordMasterKey, true))
                {
                    rjm.Key = sbwKey.Bytes;
                    using (ICryptoTransform decryptor = rjm.CreateDecryptor())
                    {
                        byte[] pass = Convert.FromBase64String(EncryptedCharacterName);

                        using (SecureBytesWrapper sbw = new SecureBytesWrapper())
                        {
                            sbw.Bytes = decryptor.TransformFinalBlock(pass, 0, pass.Length);

                            SecureCharacterName = new System.Security.SecureString();
                            foreach (char c in Encoding.Unicode.GetChars(sbw.Bytes))
                            {
                                SecureCharacterName.AppendChar(c);
                            }
                            SecureCharacterName.MakeReadOnly();
                        }
                    }
                }
            }
        }
        #endregion


        Token _TranquilityToken;
        /// <summary>
        /// AccessToken for Tranquility. Lasts up to 11 hours?
        /// </summary>
        [XmlIgnore]
        public Token TranquilityToken { get { return _TranquilityToken; } set { _TranquilityToken = value; OnPropertyChanged("TranquilityToken"); } }

        Token _SisiToken;
        /// <summary>
        /// AccessToken for Singularity. Lasts up to 11 hours?
        /// </summary>
        [XmlIgnore]
        public Token SisiToken { get { return _SisiToken; } set { _SisiToken = value; OnPropertyChanged("SisiToken"); } }

        #region Refresh Tokens
        /* This section is for experimental implemtnation using Refresh Tokens, which are used by the official EVE Launcher and described as insecure.
         * They ultimately need the same encrypted storage care as a Password. May or may not be worth implementing. 
         * The code will not compile at this time if enabled.
         */
#if REFRESH_TOKENS
        string _SisiRefreshToken;
        public string SisiRefreshToken { get { return _SisiRefreshToken; } set { _SisiRefreshToken = value; OnPropertyChanged("SisiRefreshToken"); } }

        string _TranquilityRefreshToken;
        public string TranquilityRefreshToken { get { return _TranquilityRefreshToken; } set { _TranquilityRefreshToken = value; OnPropertyChanged("TranquilityRefreshToken"); } }

        public void GetTokensFromCode(bool sisi, string code)
        {
            string uri = "https://client.eveonline.com/launcher/en/SSOVerifyUser";
            if (sisi)
            {
                uri = "https://testclient.eveonline.com/launcher/en/SSOVerifyUser";
            }

            HttpWebRequest req = (HttpWebRequest)HttpWebRequest.Create(uri);
            req.Timeout = 5000;
            req.AllowAutoRedirect = true;
            /*
            if (!sisi)
            {
                req.Headers.Add("Origin", "https://login.eveonline.com");
            }
            else
            {
                req.Headers.Add("Origin", "https://sisilogin.testeveonline.com");
            }
            /**/
            //req.Referer = uri;
            //req.CookieContainer = Cookies;
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            byte[] body = Encoding.ASCII.GetBytes(String.Format("authCode={0}", Uri.EscapeDataString(code)));
            req.ContentLength = body.Length;
            using (Stream reqStream = req.GetRequestStream())
            {
                reqStream.Write(body, 0, body.Length);
            }

            string refreshCode;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                // https://login.eveonline.com/launcher?client_id=eveLauncherTQ#access_token=...&token_type=Bearer&expires_in=43200
                string responseBody = null;
                using (Stream stream = resp.GetResponseStream())
                {
                    using (StreamReader sr = new StreamReader(stream))
                    {
                        responseBody = sr.ReadToEnd();
                    }
                }

                /*
<span id="ValidationContainer"><div class="validation-summary-errors"><span>Login failed. Possible reasons can be:</span>
<ul><li>Invalid username / password</li>
</ul></div></span>
                 */

                //                https://login.eveonline.com/launcher?client_id=eveLauncherTQ#access_token=l4nGki1CTUI7pCQZoIdnARcCLqL6ZGJM1X1tPf1bGKSJxEwP8lk_shS19w3sjLzyCbecYAn05y-Vbs-Jm1d1cw2&token_type=Bearer&expires_in=43200
                //accessToken = new Token(resp.ResponseUri);
                //refreshCode = HttpUtility.ParseQueryString(resp.ResponseUri.Query).Get("code");

                // String expires_in = HttpUtility.ParseQueryString(fromUri.Fragment).Get("expires_in");

                throw new NotImplementedException();
                // responseBody should now be JSON containing the needed tokens.
            }

        }

        public LoginResult GetRefreshToken(bool sisi, out string refreshToken)
        {
            string checkToken = sisi ? SisiRefreshToken : TranquilityRefreshToken;
            if (!string.IsNullOrEmpty(checkToken))
            {
                refreshToken = checkToken;
                return LoginResult.Success;
            }

            // need PlaintextPassword.
            if (SecurePassword == null || SecurePassword.Length == 0)
            {
                Windows.EVELogin el = new Windows.EVELogin(this, false);
                bool? result = el.ShowDialog();

                if (SecurePassword == null || SecurePassword.Length == 0)
                {
                    // password is required, sorry dude
                    refreshToken = null;
                    return LoginResult.InvalidUsernameOrPassword;
                }
            }

            string uri = "https://login.eveonline.com/Account/LogOn?ReturnUrl=%2Foauth%2Fauthorize%2F%3Fclient_id%3DeveLauncherTQ%26lang%3Den%26response_type%3Dcode%26redirect_uri%3Dhttps%3A%2F%2Flogin.eveonline.com%2Flauncher%3Fclient_id%3DeveLauncherTQ%26scope%3DeveClientToken%2520user";
            if (sisi)
            {
                uri = "https://sisilogin.testeveonline.com/Account/LogOn?ReturnUrl=%2Foauth%2Fauthorize%2F%3Fclient_id%3DeveLauncherTQ%26lang%3Den%26response_type%3Dcode%26redirect_uri%3Dhttps%3A%2F%2Fsisilogin.testeveonline.com%2Flauncher%3Fclient_id%3DeveLauncherTQ%26scope%3DeveClientToken%2520user";
            }

            HttpWebRequest req = (HttpWebRequest)HttpWebRequest.Create(uri);
            req.Timeout = 5000;
            req.AllowAutoRedirect = true;
            if (!sisi)
            {
                req.Headers.Add("Origin", "https://login.eveonline.com");
            }
            else
            {
                req.Headers.Add("Origin", "https://sisilogin.testeveonline.com");
            }
            req.Referer = uri;
            req.CookieContainer = Cookies;
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            using (SecureBytesWrapper body = new SecureBytesWrapper())
            {
                byte[] body1 = Encoding.ASCII.GetBytes(String.Format("UserName={0}&Password=", Uri.EscapeDataString(Username)));
                using (SecureStringWrapper ssw = new SecureStringWrapper(SecurePassword, Encoding.ASCII))
                {
                    using (SecureBytesWrapper escapedPassword = new SecureBytesWrapper())
                    {
                        escapedPassword.Bytes = System.Web.HttpUtility.UrlEncodeToBytes(ssw.ToByteArray());

                        body.Bytes = new byte[body1.Length + escapedPassword.Bytes.Length];
                        System.Buffer.BlockCopy(body1, 0, body.Bytes, 0, body1.Length);
                        System.Buffer.BlockCopy(escapedPassword.Bytes, 0, body.Bytes, body1.Length, escapedPassword.Bytes.Length);
                    }
                }

                req.ContentLength = body.Bytes.Length;
                using (Stream reqStream = req.GetRequestStream())
                {
                    reqStream.Write(body.Bytes, 0, body.Bytes.Length);
                }
            }

            string refreshCode;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                // https://login.eveonline.com/launcher?client_id=eveLauncherTQ#access_token=...&token_type=Bearer&expires_in=43200
                string responseBody = null;
                using (Stream stream = resp.GetResponseStream())
                {
                    using (StreamReader sr = new StreamReader(stream))
                    {
                        responseBody = sr.ReadToEnd();
                    }
                }

                if (responseBody.Contains("Invalid username / password"))
                {
                    refreshToken = null;
                    return LoginResult.InvalidUsernameOrPassword;
                }


                /*
<span id="ValidationContainer"><div class="validation-summary-errors"><span>Login failed. Possible reasons can be:</span>
<ul><li>Invalid username / password</li>
</ul></div></span>
                 */

                //                https://login.eveonline.com/launcher?client_id=eveLauncherTQ#access_token=l4nGki1CTUI7pCQZoIdnARcCLqL6ZGJM1X1tPf1bGKSJxEwP8lk_shS19w3sjLzyCbecYAn05y-Vbs-Jm1d1cw2&token_type=Bearer&expires_in=43200
                //accessToken = new Token(resp.ResponseUri);
                refreshCode = HttpUtility.ParseQueryString(resp.ResponseUri.Query).Get("code");

                    // String expires_in = HttpUtility.ParseQueryString(fromUri.Fragment).Get("expires_in");
            }

            GetTokensFromCode(sisi,refreshCode);
            throw new NotImplementedException();

            if (!sisi)
            {
                TranquilityRefreshToken = refreshToken;
            }
            else
            {
                SisiRefreshToken = refreshToken;
            }

            return LoginResult.Success;
        }
#endif
        #endregion




        public LoginResult GetSecurityWarningChallenge(bool sisi, string responseBody, out Token accessToken)
        {
            /*
            Windows.SecurityWarningWindow swWindow = new Windows.SecurityWarningWindow(responseBody);
            swWindow.ShowDialog();

            // /oauth/authorize/?client_id=eveLauncherTQ&lang=en&response_type=token&redirect_uri=https://login.eveonline.com/launcher?client_id=eveLauncherTQ&scope=eveClientToken
      
            if (string.IsNullOrEmpty( swWindow.URI))
            {
                SecurePassword = null;
                accessToken = null;
                return LoginResult.SecurityWarningClosed;
            }
            */

            //string uri = "https://login.eveonline.com/oauth/authorize/?client_id=eveLauncherTQ&lang=en&response_type=token&redirect_uri=https://login.eveonline.com/launcher?client_id=eveLauncherTQ&scope=eveClientToken";
            string uri = "https://login.eveonline.com/v2/oauth/authorize?client_id=eveLauncherTQ&amp;response_type=code&amp;scope=eveClientLogin%20cisservice.customerRead.v1%20cisservice.customerWrite.v1&amp;redirect_uri=https%3A%2F%2Flogin.eveonline.com%2Flauncher%3Fclient_id%3DeveLauncherTQ&amp;state=5617f90c-efdb-41a1-b00d-6f4f24bbeee4&amp;code_challenge_method=S256&amp;code_challenge=nC-B19HKX8ZZYfOEN_bg-YZSjVAMieqEB3nJXFyfQQc&amp;ignoreClientStyle=true&amp;showRemember=true ";

            if (sisi)
            {
                uri = "https://sisilogin.testeveonline.com/oauth/authorize/?client_id=eveLauncherTQ&lang=en&response_type=token&redirect_uri=https://sisilogin.testeveonline.com/launcher?client_id=eveLauncherTQ&scope=eveClientToken";
            }

            var req = Utils.CreateGetRequest(new Uri(uri), sisi, true, uri, Cookies);
            return GetAccessToken(sisi, req, out accessToken);

        }

        public LoginResult GetEmailChallenge(bool sisi, string responseBody, out Token accessToken)
        {
            Windows.EmailChallengeWindow emailWindow = new Windows.EmailChallengeWindow(responseBody);
            emailWindow.ShowDialog();
            if (!emailWindow.DialogResult.HasValue || !emailWindow.DialogResult.Value)
            {
                SecurePassword = null;
                accessToken = null;
                return LoginResult.EmailVerificationRequired;
            }
            SecurePassword = null;
            accessToken = null;
            return LoginResult.EmailVerificationRequired;
        }


        public LoginResult GetEULAChallenge(bool sisi, string responseBody, out Token accessToken)
        {
            Windows.EVEEULAWindow eulaWindow = new Windows.EVEEULAWindow(responseBody);
            eulaWindow.ShowDialog();
            if (!eulaWindow.DialogResult.HasValue || !eulaWindow.DialogResult.Value)
            {
                SecurePassword = null;
                accessToken = null;
                return LoginResult.EULADeclined;
            }

            string uri = "https://login.eveonline.com/OAuth/Eula";
            if (sisi)
            {
                uri = "https://sisilogin.testeveonline.com/OAuth/Eula";
            }


            HttpWebRequest req = (HttpWebRequest)HttpWebRequest.Create(uri);
            req.Timeout = 30000;
            req.AllowAutoRedirect = true;
            if (!sisi)
            {
                req.Headers.Add("Origin", "https://login.eveonline.com");
            }
            else
            {
                req.Headers.Add("Origin", "https://sisilogin.testeveonline.com");
            }
            req.Referer = uri;
            req.CookieContainer = Cookies;
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            using (SecureBytesWrapper body = new SecureBytesWrapper())
            {
                string eulaHash = Utils.GetEulaHashFromBody(responseBody);
                string returnUrl = Utils.GetEulaReturnUrlFromBody(responseBody);

                string formattedString = String.Format("eulaHash={0}&returnUrl={1}&action={2}", Uri.EscapeDataString(eulaHash), Uri.EscapeDataString(returnUrl), "Accept");
                body.Bytes = Encoding.ASCII.GetBytes(formattedString);

                req.ContentLength = body.Bytes.Length;
                try
                {
                    using (Stream reqStream = req.GetRequestStream())
                    {
                        reqStream.Write(body.Bytes, 0, body.Bytes.Length);
                    }
                }
                catch (System.Net.WebException e)
                {
                    switch (e.Status)
                    {
                        case WebExceptionStatus.Timeout:
                            {
                                accessToken = null;
                                return LoginResult.Timeout;
                            }
                        default:
                            throw;
                    }
                }
            }
            try
            {
                return GetAccessToken(sisi, req, out accessToken);
            }
            catch (System.Net.WebException we)
            {
                return GetAccessToken(sisi, out accessToken);
            }
        }


        public LoginResult GetEmailCodeChallenge(bool sisi, string responseBody, out Token accessToken)
        {
            /*
            string IsPasswordBreached;
            string NumPasswordBreaches;
            string uriPart;
            GetValueBetween(responseBody, "<input id=\"IsPasswordBreached\" name=\"IsPasswordBreached\" type=\"hidden\" value=\"", "\" />", out IsPasswordBreached);
            GetValueBetween(responseBody, "<input id=\"NumPasswordBreaches\" name=\"NumPasswordBreaches\" type=\"hidden\" value=\"", "\" />", out NumPasswordBreaches);

            GetValueBetween(responseBody, "<form action=\"", "\" method=\"post\">", out uriPart);
            /**/

            Windows.VerificationCodeChallengeWindow acw = new Windows.VerificationCodeChallengeWindow(this);
            acw.ShowDialog();
            if (!acw.DialogResult.HasValue || !acw.DialogResult.Value)
            {
                SecurePassword = null;
                accessToken = null;
                return LoginResult.InvalidEmailVerificationChallenge;
            }

            //string origin = "https://login.eveonline.com";
            //string uri;
            ////if (!string.IsNullOrEmpty(uriPart))
            ////{
            ////    uri = origin + uriPart;
            //// }
            //// else
            //{
            //    if (sisi)
            //    {

            //        uri = "https://sisilogin.testeveonline.com/account/verifytwofactor?ReturnUrl=%2Foauth%2Fauthorize%2F%3Fclient_id%3DeveLauncherTQ%26lang%3Den%26response_type%3Dtoken%26redirect_uri%3Dhttps%3A%2F%2Fsisilogin.testeveonline.com%2Flauncher%3Fclient_id%3DeveLauncherTQ%26scope%3DeveClientToken";
            //    }
            //    else
            //    {
            //        uri = "https://login.eveonline.com/account/verifytwofactor?ReturnUrl=%2Foauth%2Fauthorize%2F%3Fclient_id%3DeveLauncherTQ%26lang%3Den%26response_type%3Dtoken%26redirect_uri%3Dhttps%3A%2F%2Flogin.eveonline.com%2Flauncher%3Fclient_id%3DeveLauncherTQ%26scope%3DeveClientToken";
            //    }
            //}

            //"POST /account/verifytwofactor?ReturnUrl=%2Fv2%2Foauth%2Fauthorize%3Fclient_id%3DeveLauncherTQ%26response_type%3Dcode%26scope%3DeveClientLogin%2520cisservice.customerRead.v1%2520cisservice.customerWrite.v1%26redirect_uri%3Dhttps%253A%252F%252Fsisilogin.testeveonline.com%252Flauncher%253Fclient_id%253DeveLauncherTQ%26state%3D1043d900-ab13-42f3-a741-285cce0c8b47%26code_challenge_method%3DS256%26code_challenge%3DC0emnYPGUFfgXiyQx9d47zMM3uUXb6H9JB-PLptvtZ4%26ignoreClientStyle%3Dtrue%26showRemember%3Dtrue HTTP/1.1"

            var uri = Utils.GetVerifyTwoFactorUri(sisi, state.ToString(), challengeHash);
            var req = Utils.CreatePostRequest(uri, sisi, true, null, Cookies);

            //HttpWebRequest req = (HttpWebRequest)HttpWebRequest.Create(uri);
            //req.Timeout = 30000;
            //req.AllowAutoRedirect = true;
            //if (!sisi)
            //{
            //    req.Headers.Add("Origin", "https://login.eveonline.com");
            //}
            //else
            //{
            //    req.Headers.Add("Origin", "https://sisilogin.testeveonline.com");
            //}
            //req.Referer = uri;
            //req.CookieContainer = Cookies;
            //req.Method = "POST";
            //req.ContentType = "application/x-www-form-urlencoded";
            using (SecureBytesWrapper body = new SecureBytesWrapper())
            {
                //                body.Bytes = Encoding.ASCII.GetBytes(String.Format("Challenge={0}&IsPasswordBreached={1}&NumPasswordBreaches={2}&command={3}", Uri.EscapeDataString(acw.VerificationCode), IsPasswordBreached, NumPasswordBreaches, "Continue"));
                body.Bytes = Encoding.ASCII.GetBytes(String.Format("Challenge={0}&command={1}", Uri.EscapeDataString(acw.VerificationCode), "Continue"));

                req.ContentLength = body.Bytes.Length;
                try
                {
                    using (Stream reqStream = req.GetRequestStream())
                    {
                        reqStream.Write(body.Bytes, 0, body.Bytes.Length);
                    }
                }
                catch (System.Net.WebException e)
                {
                    switch (e.Status)
                    {
                        case WebExceptionStatus.Timeout:
                            {
                                accessToken = null;
                                return LoginResult.Timeout;
                            }
                        default:
                            throw;
                    }
                }
            }
            LoginResult result = GetAccessToken(sisi, req, out accessToken);
            if (result == LoginResult.Success)
            {
                // successful verification code challenge, make sure we save the cookies.
                App.Settings.Store();
            }
            return result;
        }

        public LoginResult GetAuthenticatorChallenge(bool sisi, out Token accessToken)
        {
            Windows.AuthenticatorChallengeWindow acw = new Windows.AuthenticatorChallengeWindow(this);
            acw.ShowDialog();
            if (!acw.DialogResult.HasValue || !acw.DialogResult.Value)
            {
                SecurePassword = null;
                accessToken = null;
                return LoginResult.InvalidAuthenticatorChallenge;
            }

            string uri = "https://login.eveonline.com/account/authenticator?ReturnUrl=%2Foauth%2Fauthorize%2F%3Fclient_id%3DeveLauncherTQ%26lang%3Den%26response_type%3Dtoken%26redirect_uri%3Dhttps%3A%2F%2Flogin.eveonline.com%2Flauncher%3Fclient_id%3DeveLauncherTQ%26scope%3DeveClientToken";
            if (sisi)
            {
                uri = "https://sisilogin.testeveonline.com/account/authenticator?ReturnUrl=%2Foauth%2Fauthorize%2F%3Fclient_id%3DeveLauncherTQ%26lang%3Den%26response_type%3Dtoken%26redirect_uri%3Dhttps%3A%2F%2Fsisilogin.testeveonline.com%2Flauncher%3Fclient_id%3DeveLauncherTQ%26scope%3DeveClientToken";
            }

            HttpWebRequest req = (HttpWebRequest)HttpWebRequest.Create(uri);
            req.Timeout = 30000;
            req.AllowAutoRedirect = true;
            if (!sisi)
            {
                req.Headers.Add("Origin", "https://login.eveonline.com");
            }
            else
            {
                req.Headers.Add("Origin", "https://sisilogin.testeveonline.com");
            }
            req.Referer = uri;
            req.CookieContainer = Cookies;
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            using (SecureBytesWrapper body = new SecureBytesWrapper())
            {
                body.Bytes = Encoding.ASCII.GetBytes(String.Format("Challenge={0}&RememberTwoFactor={1}&command={2}", Uri.EscapeDataString(acw.AuthenticatorCode), "true", "Continue"));

                req.ContentLength = body.Bytes.Length;
                try
                {
                    using (Stream reqStream = req.GetRequestStream())
                    {
                        reqStream.Write(body.Bytes, 0, body.Bytes.Length);
                    }
                }
                catch (System.Net.WebException e)
                {
                    switch (e.Status)
                    {
                        case WebExceptionStatus.Timeout:
                            {
                                accessToken = null;
                                return LoginResult.Timeout;
                            }
                        default:
                            throw;
                    }
                }
            }
            LoginResult result = GetAccessToken(sisi, req, out accessToken);
            if (result == LoginResult.Success)
            {
                // successful authenticator challenge, make sure we save the cookies.
                App.Settings.Store();
            }
            return result;
        }

        public LoginResult GetCharacterChallenge(bool sisi, out Token accessToken)
        {
            // need SecureCharacterName.
            if (SecureCharacterName == null || SecureCharacterName.Length == 0)
            {
                DecryptCharacterName(true);
                if (SecureCharacterName == null || SecureCharacterName.Length == 0)
                {

                    Windows.CharacterChallengeWindow ccw = new Windows.CharacterChallengeWindow(this);
                    bool? result = ccw.ShowDialog();

                    if (string.IsNullOrWhiteSpace(ccw.CharacterName))
                    {
                        // CharacterName is required, sorry dude
                        accessToken = null;
                        //  SecurePassword = null;
                        SecureCharacterName = null;
                        return LoginResult.InvalidCharacterChallenge;
                    }

                    SecureCharacterName = new System.Security.SecureString();
                    foreach (char c in ccw.CharacterName)
                    {
                        SecureCharacterName.AppendChar(c);
                    }
                    SecureCharacterName.MakeReadOnly();
                    EncryptCharacterName();
                    App.Settings.Store();
                }
            }

            string uri = "https://login.eveonline.com/Account/Challenge?ReturnUrl=%2Foauth%2Fauthorize%2F%3Fclient_id%3DeveLauncherTQ%26lang%3Den%26response_type%3Dtoken%26redirect_uri%3Dhttps%3A%2F%2Flogin.eveonline.com%2Flauncher%3Fclient_id%3DeveLauncherTQ%26scope%3DeveClientToken";
            if (sisi)
            {
                uri = "https://sisilogin.testeveonline.com/Account/Challenge?ReturnUrl=%2Foauth%2Fauthorize%2F%3Fclient_id%3DeveLauncherTQ%26lang%3Den%26response_type%3Dtoken%26redirect_uri%3Dhttps%3A%2F%2Fsisilogin.testeveonline.com%2Flauncher%3Fclient_id%3DeveLauncherTQ%26scope%3DeveClientToken";
            }

            HttpWebRequest req = (HttpWebRequest)HttpWebRequest.Create(uri);
            req.Timeout = 30000;
            req.AllowAutoRedirect = true;
            if (!sisi)
            {
                req.Headers.Add("Origin", "https://login.eveonline.com");
            }
            else
            {
                req.Headers.Add("Origin", "https://sisilogin.testeveonline.com");
            }
            req.Referer = uri;
            req.CookieContainer = Cookies;
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            using (SecureBytesWrapper body = new SecureBytesWrapper())
            {
                byte[] body1 = Encoding.ASCII.GetBytes(String.Format("RememberCharacterChallenge={0}&Challenge=", "true"));
                using (SecureStringWrapper ssw = new SecureStringWrapper(SecureCharacterName, Encoding.ASCII))
                {
                    using (SecureBytesWrapper escapedCharacterName = new SecureBytesWrapper())
                    {
                        escapedCharacterName.Bytes = System.Web.HttpUtility.UrlEncodeToBytes(ssw.ToByteArray());

                        body.Bytes = new byte[body1.Length + escapedCharacterName.Bytes.Length];
                        System.Buffer.BlockCopy(body1, 0, body.Bytes, 0, body1.Length);
                        System.Buffer.BlockCopy(escapedCharacterName.Bytes, 0, body.Bytes, body1.Length, escapedCharacterName.Bytes.Length);
                    }
                }

                req.ContentLength = body.Bytes.Length;
                try
                {
                    using (Stream reqStream = req.GetRequestStream())
                    {
                        reqStream.Write(body.Bytes, 0, body.Bytes.Length);
                    }
                }
                catch (System.Net.WebException e)
                {
                    switch (e.Status)
                    {
                        case WebExceptionStatus.Timeout:
                            {
                                accessToken = null;
                                return LoginResult.Timeout;
                            }
                        default:
                            throw;
                    }
                }
            }
            return GetAccessToken(sisi, req, out accessToken);
        }

        public LoginResult GetAccessToken(bool sisi, HttpWebRequest req, out Token accessToken)
        {
            accessToken = null;
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    // https://login.eveonline.com/launcher?client_id=eveLauncherTQ#access_token=...&token_type=Bearer&expires_in=43200
                    //https://login.eveonline.com/v2/oauth/authorize?client_id=eveLauncherTQ&response_type=code&scope=eveClientLogin%20cisservice.customerRead.v1%20cisservice.customerWrite.v1&redirect_uri=https%3A%2F%2Flogin.eveonline.com%2Flauncher%3Fclient_id%3DeveLauncherTQ&state=5617f90c-efdb-41a1-b00d-6f4f24bbeee4&code_challenge_method=S256&code_challenge=nC-B19HKX8ZZYfOEN_bg-YZSjVAMieqEB3nJXFyfQQc&ignoreClientStyle=true&showRemember=true HTTP/1.1

                    string responseBody = null;
                    responseBody = Utils.GetResponseBody(resp);
                    UpdateCookieStorage();

                    /*
<span id="ValidationContainer"><div class="validation-summary-errors"><span>Login failed. Possible reasons can be:</span>
<ul><li>Invalid username / password</li>
<li>Incorrect character name entered</li>
</ul></div></span>
                     */
                    if (responseBody.Contains("Incorrect character name entered"))
                    {
                        accessToken = null;
                        SecurePassword = null;
                        SecureCharacterName = null;
                        return LoginResult.InvalidCharacterChallenge;
                    }

                    /*
    <span id="ValidationContainer"><div class="validation-summary-errors"><span>Login failed. Possible reasons can be:</span>
    <ul><li>Invalid username / password</li>
    </ul></div></span>
                     */

                    if (responseBody.Contains("Invalid username / password"))
                    {
                        accessToken = null;
                        SecurePassword = null;
                        return LoginResult.InvalidUsernameOrPassword;
                    }

                    // I'm just guessing on this one at the moment.
                    if (responseBody.Contains("Invalid authenticat"))
                    {
                        accessToken = null;
                        SecurePassword = null;
                        return LoginResult.InvalidAuthenticatorChallenge;
                    }
                    //The 2FA page now has "Character challenge" in the text but it is hidden. This should fix it from
                    //Coming up during 2FA challenge
                    if (responseBody.Contains("Character challenge") && !responseBody.Contains("visuallyhidden"))
                    {
                        return GetCharacterChallenge(sisi, out accessToken);
                    }

                    if (responseBody.Contains("Email verification required"))
                    {
                        return GetEmailChallenge(sisi, responseBody, out accessToken);
                    }

                    if (responseBody.Contains("Authenticator is enabled"))
                    {
                        return GetAuthenticatorChallenge(sisi, out accessToken);
                    }

                    if (responseBody.Contains("Please enter the verification code "))
                    {
                        return GetEmailCodeChallenge(sisi, responseBody, out accessToken);
                    }

                    if (responseBody.Contains("Security Warning"))
                    {
                        return GetSecurityWarningChallenge(sisi, responseBody, out accessToken);
                    }

                    if (responseBody.ToLower().Contains("form action=\"/oauth/eula\""))
                    {
                        return GetEULAChallenge(sisi, responseBody, out accessToken);
                    }

                    try
                    {
                        //https://login.eveonline.com/launcher?client_id=eveLauncherTQ#access_token=l4nGki1CTUI7pCQZoIdnARcCLqL6ZGJM1X1tPf1bGKSJxEwP8lk_shS19w3sjLzyCbecYAn05y-Vbs-Jm1d1cw2&token_type=Bearer&expires_in=43200
                        authCode = HttpUtility.ParseQueryString(resp.ResponseUri.ToString()).Get("code");
                        if (authCode == null)
                        {
                            responseBody = resp.ResponseUri + 
                                    Environment.NewLine +
                                    Environment.NewLine +
                                    Environment.NewLine +
                                    Utils.GetResponseBody(resp);
                            return LoginResult.Error;
                        }
                        GetAccessToken(sisi, authCode, out responseBody);
                        accessToken = new Token(JsonConvert.DeserializeObject<authObj>(responseBody));
                    }
                    catch (Exception e)
                    {
                        Windows.UnhandledResponseWindow urw = new Windows.UnhandledResponseWindow(responseBody);
                        urw.ShowDialog();

                        // can't get the token
                        accessToken = null;
                        SecurePassword = null;
                        return LoginResult.TokenFailure;
                    }

                }

                if (!sisi)
                {
                    TranquilityToken = accessToken;
                }
                else
                {
                    SisiToken = accessToken;
                }

                return LoginResult.Success;
            }
            catch (System.Net.WebException we)
            {
                switch (we.Status)
                {
                    case WebExceptionStatus.Timeout:
                        return LoginResult.Timeout;
                    default:
                        string responseBody = Utils.GetResponseBody(we.Response);

                        Windows.UnhandledResponseWindow urw = new Windows.UnhandledResponseWindow(responseBody);
                        urw.ShowDialog();
                        return LoginResult.Error;
                }
            }
        }


        public class authObj
        {
            private int _expiresIn;
            public string access_token { get; set; }
            public int expires_in
            {
                get
                {
                    return _expiresIn;
                }
                set
                {
                    _expiresIn = value;
                    Expiration = DateTime.Now.AddMinutes(_expiresIn);
                }
            }
            public string token_type { get; set; }
            public string refresh_token { get; set; }

            public DateTime Expiration { get; private set; }

        }


        private LoginResult GetAccessToken(bool sisi, string authCode, out string responseBody)
        {
            HttpWebRequest req2 = Utils.CreatePostRequest(new Uri(Utils.token, UriKind.Relative), sisi, true, Utils.refererUri, Cookies);



            //byte[] body =
            //    Encoding.UTF8.GetBytes("grant_type=authorization_code&client_id=eveLauncherTQ&redirect_uri=" + HttpUtility.UrlEncode(sisi?Utils.sisiBaseUri:Utils.tqBaseUri) + "%2Flauncher%3Fclient_id%3DeveLauncherTQ&code=" + authCode + "&code_verifier=" +
            //Base64UrlEncoder.Encode(challengeCode));

            req2.SetBody(Utils.GetSsoTokenRequestBody(sisi, authCode, challengeCode));

            return Utils.GetHttpWebResponse(req2, UpdateCookieStorage, out responseBody);

        }

        public LoginResult GetRequestVerificationToken(Uri uri, bool sisi, out string verificationToken)
        {
            string responseBody;
            verificationToken = null;

            var req = Utils.CreateGetRequest(uri, sisi, false, Utils.refererUri, Cookies);
            req.ContentLength = 0;

            var result = Utils.GetHttpWebResponse(req, UpdateCookieStorage, out responseBody);

            if (result == LoginResult.Success)
            {
                verificationToken = Utils.GetRequestVerificationTokenFromBody(responseBody);
            }

            return result;
        }


        public LoginResult GetAccessToken(bool sisi, out Token accessToken)
        {
            Token checkToken = sisi ? SisiToken : TranquilityToken;
            if (checkToken != null && !checkToken.IsExpired)
            {
                accessToken = checkToken;
                return LoginResult.Success;
            }

            // need SecurePassword.
            if (SecurePassword == null || SecurePassword.Length == 0)
            {
                DecryptPassword(true);
                if (SecurePassword == null || SecurePassword.Length == 0)
                {

                    Windows.EVELogin el = new Windows.EVELogin(this, true);
                    bool? dialogResult = el.ShowDialog();

                    if (SecurePassword == null || SecurePassword.Length == 0)
                    {
                        // password is required, sorry dude
                        accessToken = null;
                        return LoginResult.InvalidUsernameOrPassword;
                    }

                    App.Settings.Store();
                }
            }


            var uri = Utils.GetLoginUri(sisi, state.ToString(), challengeHash);

            string RequestVerificationToken = string.Empty;
            var result = GetRequestVerificationToken(uri, sisi, out RequestVerificationToken);

            var req = Utils.CreatePostRequest(uri, sisi, true, Utils.refererUri, Cookies);

            using (SecureBytesWrapper body = new SecureBytesWrapper())
            {
                byte[] body1 = Encoding.ASCII.GetBytes(String.Format("__RequestVerificationToken={1}&UserName={0}&Password=", Uri.EscapeDataString(Username), Uri.EscapeDataString(RequestVerificationToken)));
                //                byte[] body1 = Encoding.ASCII.GetBytes(String.Format("UserName={0}&Password=", Uri.EscapeDataString(Username)));
                using (SecureStringWrapper ssw = new SecureStringWrapper(SecurePassword, Encoding.ASCII))
                {
                    using (SecureBytesWrapper escapedPassword = new SecureBytesWrapper())
                    {
                        escapedPassword.Bytes = System.Web.HttpUtility.UrlEncodeToBytes(ssw.ToByteArray());

                        body.Bytes = new byte[body1.Length + escapedPassword.Bytes.Length];
                        System.Buffer.BlockCopy(body1, 0, body.Bytes, 0, body1.Length);
                        System.Buffer.BlockCopy(escapedPassword.Bytes, 0, body.Bytes, body1.Length, escapedPassword.Bytes.Length);
                        req.SetBody(body);
                    }
                }
            }

            return GetAccessToken(sisi, req, out accessToken);
        }

        public LoginResult GetSSOToken(bool sisi, out Token ssoToken)
        {
            Token accessToken;
            LoginResult lr = this.GetAccessToken(sisi, out ssoToken);
           
            return lr;
        }

        public LoginResult Launch(string sharedCachePath, bool sisi, DirectXVersion dxVersion, long characterID)
        {
            Token ssoToken;
            LoginResult lr = GetSSOToken(sisi, out ssoToken);
            if (lr != LoginResult.Success)
                return lr;
            if (!App.Launch(sharedCachePath, sisi, dxVersion, characterID, ssoToken))
                return LoginResult.Error;

            return LoginResult.Success;
        }

        public LoginResult Launch(string gameName, string gameProfileName, bool sisi, DirectXVersion dxVersion, long characterID)
        {
            //string ssoToken;
            //LoginResult lr = GetSSOToken(sisi, out ssoToken);
            //if (lr != LoginResult.Success)
            //    return lr;
            if (!App.Launch(gameName, gameProfileName, sisi, dxVersion, characterID, TranquilityToken))
                return LoginResult.Error;

            return LoginResult.Success;
        }


        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;

        public void FirePropertyChanged(string value)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(value));
            }
        }
        public void OnPropertyChanged(string value)
        {
            FirePropertyChanged(value);
        }
        #endregion


        public void Dispose()
        {
            if (this.SecurePassword != null)
            {
                this.SecurePassword.Dispose();
                this.SecurePassword = null;
            }
            this.EncryptedPassword = null;
            this.EncryptedPasswordIV = null;
            if (this.SecureCharacterName != null)
            {
                this.SecureCharacterName.Dispose();
                this.SecureCharacterName = null;
            }
            this.EncryptedCharacterName = null;
            this.EncryptedCharacterNameIV = null;

            ISBoxerEVELauncher.CookieStorage.DeleteCookies(this);
            this.Username = null;
            this.Cookies = null;
            //this.NewCookieStorage = null;
        }

        public override string ToString()
        {
            return Username;
        }

        EVEAccount Launchers.ILaunchTarget.EVEAccount
        {
            get { return this; }
        }

        public long CharacterID
        {
            get { return 0; }
        }
    }
}
