﻿using Box.V2.Auth;
using Box.V2.Config;
using Box.V2.Converter;
using Box.V2.Managers;
using Box.V2.Request;
using Box.V2.Services;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Box.V2.Test
{
    public abstract class BoxResourceManagerTest 
    {

        protected IBoxConverter _converter;
        protected Mock<IRequestHandler> _handler;
        protected IBoxService _service;
        protected Mock<IBoxConfig> _config;
        protected AuthRepository _authRepository;

        protected Uri _baseUri = new Uri(Constants.BoxApiUriString);
        protected Uri _FilesUri = new Uri(Constants.FilesEndpointString);
        protected Uri _FilesUploadUri = new Uri(Constants.FilesUploadEndpointString);

        public BoxResourceManagerTest()
        {
            // Initial Setup
            _converter = new BoxJsonConverter();
            _handler = new Mock<IRequestHandler>();
            _service = new BoxService(_handler.Object);
            _config = new Mock<IBoxConfig>();

            _config.SetupGet(x => x.FilesEndpointUri).Returns(_FilesUri);
            _config.SetupGet(x => x.FilesUploadEndpointUri).Returns(_FilesUploadUri);

            _authRepository = new AuthRepository(_config.Object, _service, _converter, new OAuthSession("fakeAccessToken", "fakeRefreshToken", 3600, "bearer"));
        }

        public static bool AreJsonStringsEqual(string sourceJsonString, string targetJsonString)
        {
            JObject sourceJObject = JsonConvert.DeserializeObject<JObject>(sourceJsonString);
            JObject targetJObject = JsonConvert.DeserializeObject<JObject>(targetJsonString);

            return JToken.DeepEquals(sourceJObject, targetJObject);
        }
        public static T CreateInstanceNonPublicConstructor<T>()
        {
            Type[] pTypes = new Type[0];
          
            ConstructorInfo[] c = typeof(T).GetConstructors
                (BindingFlags.NonPublic | BindingFlags.Instance
                );

            T inst =
                (T)c[0].Invoke(BindingFlags.NonPublic,
                               null,
                               null,
                               System.Threading.Thread.CurrentThread.CurrentCulture);
            return inst;
        }

        public static string HexStringFromBytes(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (byte b in bytes)
            {
                var hex = b.ToString("x2");
                sb.Append(hex);
            }
            return sb.ToString();
        }
    }
}
