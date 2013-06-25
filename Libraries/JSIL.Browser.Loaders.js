JSIL.loadGlobalScriptCore = function (init, onComplete) {
  var done = false;

  var body = document.getElementsByTagName("body")[0];

  var scriptTag = document.createElement("script");

  JSIL.Browser.RegisterOneShotEventListener(
    scriptTag, "load", true, 
    function ScriptTag_Load (e) {
      if (done)
        return;

      done = true;
      onComplete(scriptTag, null);
    }
  ); 
  JSIL.Browser.RegisterOneShotEventListener(
    scriptTag, "error", true, 
    function ScriptTag_Error (e) {
      if (done)
        return;

      done = true;
      onComplete(null, e);
    }
  );

  scriptTag.type = "text/javascript";
  var initType = init(scriptTag);

  try {
    body.appendChild(scriptTag);
  } catch (exc) {
    done = true;
    onComplete(null, exc);
  }

  if (initType === "synchronous")
    onComplete(scriptTag, null);
};

JSIL.loadGlobalScriptText = function (fakeUri, text, onComplete) {
  JSIL.loadGlobalScriptCore(
    function (scriptTag) {
      var absoluteFakeUri = getAbsoluteUrl(fakeUri);
      scriptTag.setAttribute("uri", fakeUri);
      var uriPrefix = "//# sourceURL=" + absoluteFakeUri + "\r\n";
      scriptTag.textContent = uriPrefix + text;
      return "synchronous";
    },
    onComplete
  );
};

JSIL.loadGlobalScript = function (uri, onComplete) {
  var anchor = document.createElement("a");
  anchor.href = uri;
  var absoluteUri = anchor.href;

  JSIL.loadGlobalScriptCore(
    function (scriptTag) {
      scriptTag.src = absoluteUri;
      return "asynchronous";
    },
    onComplete
  );
};

var warnedAboutOpera = false;
var warnedAboutCORS = false;
var warnedAboutCORSImage = false;
var hasCORSXhr = false, hasCORSImage = false;

function getAbsoluteUrl (localUrl) {
  var temp = document.createElement("a");
  temp.href = localUrl;
  return temp.href;
};

function needCORSForUri (uri) {
  var result = jsilConfig.CORS;

  var absoluteUrl = getAbsoluteUrl(uri);
  var urlPrefix = window.location.protocol + "//" + window.location.host + "/";
  var sameHost = (absoluteUrl.indexOf(urlPrefix) >= 0);

  return result && !sameHost;
};

function doXHR (uri, asBinary, onComplete) {
  var req = null, isXDR = false;
  var needCORS = needCORSForUri(uri);

  if (location.protocol === "file:") {
    var errorText = "Loading assets from file:// is not possible in modern web browsers. You must host your application/game on a web server.";

    if (console && console.error) {
      console.error(errorText + "\nFailed to load: " + uri);
      onComplete(null, errorText);
      return;
    } else {
      throw new Error(errorText);
    }
  } else {
    req = new XMLHttpRequest();

    if (needCORS && !("withCredentials" in req)) {
      if ((!asBinary) && (typeof (XDomainRequest) !== "undefined")) {
        isXDR = true;
        req = new XDomainRequest();
      } else {
        if (!warnedAboutCORS) {
          JSIL.Host.logWriteLine("WARNING: This game requires support for CORS, and your browser does not appear to have it. Loading may fail.");
          warnedAboutCORS = true;
        }

        onComplete(null, "CORS unavailable");
        return;
      }
    }
  }

  var isDone = false;
  var releaseEventListeners = function () {
    req.onprogress = null;
    req.onload = null;
    req.onerror = null;
    req.ontimeout = null;
    req.onreadystatechange = null;
  };
  var succeeded = function (response, status, statusText) {
    if (isDone)
      return;

    isDone = true;
    releaseEventListeners();

    if (status >= 400) {
      onComplete(
        {
          response: response,
          status: status,
          statusText: statusText
        }, 
        statusText || status
      );
    } else {
      onComplete(
        {
          response: response,
          status: status,
          statusText: statusText
        }, null
      );
    }
  };
  var failed = function (error) {
    if (isDone)
      return;

    isDone = true;
    releaseEventListeners();
    
    onComplete(null, error);
  };

  if (isXDR) {
    // http://social.msdn.microsoft.com/Forums/en-US/iewebdevelopment/thread/30ef3add-767c-4436-b8a9-f1ca19b4812e
    req.onprogress = function () {};

    req.onload = function () {
      succeeded(req.responseText);
    };

    req.onerror = function () {
      failed("Unknown error");
    };

    req.ontimeout = function () {
      failed("Timed out");
    };
  } else {
    req.onreadystatechange = function (evt) {
      if (req.readyState != 4)
        return;

      if (isDone)
        return;

      if (asBinary) {
        var bytes;

        try {
          if (
            (typeof (ArrayBuffer) === "function") &&
            (typeof (req.response) === "object") &&
            (req.response !== null)
          ) {
            var buffer = req.response;
            bytes = new Uint8Array(buffer);
          } else if (
            (typeof (req.responseBody) !== "undefined") && 
            (typeof (VBArray) !== "undefined") &&
            (req.responseBody)
          ) {
            bytes = new VBArray(req.responseBody).toArray();
          } else if (req.responseText) {
            var text = req.responseText;
            bytes = JSIL.StringToByteArray(text);
          } else {
            failed("Unknown error");
            return;
          }
        } catch (exc) {
          failed(exc);
          return;
        }

        succeeded(bytes, req.status, req.statusText);
      } else {
        try {
          var responseText = req.responseText;
        } catch (exc) {
          failed(exc);
          return;
        }

        succeeded(responseText, req.status, req.statusText);
      }
    };
  }

  try {
    if (isXDR) {
      req.open('GET', uri);
    } else {
      req.open('GET', uri, true);
    }
  } catch (exc) {
    failed(exc);
  }

  if (asBinary) {
    if (typeof (ArrayBuffer) === "function") {
      req.responseType = 'arraybuffer';
    }

    if (typeof (req.overrideMimeType) !== "undefined") {
      req.overrideMimeType('application/octet-stream; charset=x-user-defined');
    }
  } else {
    if (typeof (req.overrideMimeType) !== "undefined") {
      req.overrideMimeType('text/plain; charset=x-user-defined');
    }
  }

  try {
    if (isXDR) {
      req.send(null);
    } else {
      req.send();
    }
  } catch (exc) {
    failed(exc);
  }
};

function loadTextAsync (uri, onComplete) {
  return doXHR(uri, false, function (result, error) {
    if (result)
      onComplete(result.response, error);
    else
      onComplete(null, error);
  });
};

function postProcessResultNormal (bytes) {
  return bytes;
};

function postProcessResultOpera (bytes) {
  // Opera sniffs content types on request bodies and if they're text, converts them to 16-bit unicode :|

  if (
    (bytes[1] === 0) &&
    (bytes[3] === 0) &&
    (bytes[5] === 0) &&
    (bytes[7] === 0)
  ) {
    if (!warnedAboutOpera) {
      JSIL.Host.logWriteLine("WARNING: Your version of Opera has a bug that corrupts downloaded file data. Please update to a new version or try a better browser.");
      warnedAboutOpera = true;
    }

    var resultBytes = new Array(bytes.length / 2);
    for (var i = 0, j = 0, l = bytes.length; i < l; i += 2, j += 1) {
      resultBytes[j] = bytes[i];
    }

    return resultBytes;
  } else {
    return bytes;
  }
};

function loadBinaryFileAsync (uri, onComplete) {
  var postProcessResult = postProcessResultNormal;  
  if (window.navigator.userAgent.indexOf("Opera/") >= 0) {
    postProcessResult = postProcessResultOpera;
  }

  return doXHR(uri, true, function (result, error) {
    if (result)
      onComplete(postProcessResult(result.response), error);
    else
      onComplete(null, error);
  });
};

var finishLoadingScript = function (state, path, onError, scriptText) {
  state.pendingScriptLoads += 1;

  var callback = function (result, error) {
    state.pendingScriptLoads -= 1;

    if (error) {
      var errorText = "Network request failed: " + stringifyLoadError(error);
      
      state.assetLoadFailures.push(
        [path, errorText]
      );

      if (jsilConfig.onLoadFailure) {
        try {
          jsilConfig.onLoadFailure(path, errorText);
        } catch (exc2) {
        }
      }

      onError(errorText);
    }          
  };

  if (scriptText)
    JSIL.loadGlobalScriptText(path, scriptText, callback);
  else
    JSIL.loadGlobalScript(path, callback);
};

var loadScriptInternal = function (root, args) {
  var absoluteUrl = getAbsoluteUrl(root + args.filename);

  if (absoluteUrl.indexOf("file://") === 0) {
    // No browser properly supports XHR against file://
    args.onDoneLoading(function () {
      finishLoadingScript(args.state, absoluteUrl, args.onError, null);
    });
  } else {
    args.loadText(root, function (result, error) {
      if ((result !== null) && (!error))
        args.onDoneLoading(function () {
          finishLoadingScript(args.state, absoluteUrl, args.onError, result);
        });
      else
        args.onError(error);
    });
  }
};

var assetLoaders = {
  "Library": function loadLibrary (args) {
    loadScriptInternal(jsilConfig.libraryRoot, args);
  },

  "Script": function loadScript (args) {
    loadScriptInternal(jsilConfig.scriptRoot, args);
  },

  "Image": function loadImage (args) {
    var e = document.createElement("img");
    if (jsilConfig.CORS) {
      if (hasCORSImage) {
        e.crossOrigin = "";
      } else if (hasCORSXhr && ($blobBuilderInfo.hasBlobBuilder || $blobBuilderInfo.hasBlobCtor)) {
        if (!warnedAboutCORSImage) {
          JSIL.Host.logWriteLine("WARNING: This game requires support for CORS, and your browser does not support it for images. Using workaround...");
          warnedAboutCORSImage = true;
        }

        return loadImageCORSHack(e, args);
      } else {
        if (!warnedAboutCORSImage) {
          JSIL.Host.logWriteLine("WARNING: This game requires support for CORS, and your browser does not support it.");
          warnedAboutCORSImage = true;
        }

        onError("CORS unavailable");
        return;
      }
    }

    var finisher = function () {
      $jsilbrowserstate.allAssetNames.push(args.filename);
      allAssets[getAssetName(args.filename)] = new HTML5ImageAsset(getAssetName(args.filename, true), e);
    };

    JSIL.Browser.RegisterOneShotEventListener(e, "error", true, args.onError);
    JSIL.Browser.RegisterOneShotEventListener(e, "load", true, args.onDoneLoading.bind(null, finisher));

    args.resolveUrl(
      jsilConfig.contentRoot, "", null,
      function (url) {
        e.src = url;
      }
    );
  },

  "File": function loadFile (args) {
    args.loadBytes(jsilConfig.fileRoot, function (result, error) {
      if ((result !== null) && (!error)) {
        $jsilbrowserstate.allFileNames.push(args.filename);
        allFiles[args.filename.toLowerCase()] = result;

        args.onDoneLoading(null); 
      } else {
        args.onError(error);
      }
    });
  },

  "SoundBank": function loadSoundBank (args) {
    args.loadText(jsilConfig.contentRoot, function (result, error) {
      if ((result !== null) && (!error)) {
        var finisher = function () {
          $jsilbrowserstate.allAssetNames.push(args.filename);
          allAssets[getAssetName(args.filename)] = JSON.parse(result);
        };

        args.onDoneLoading(finisher);
      } else {
        args.onError(error);
      }
    });
  },

  "Resources": function loadResources (args) {
    args.loadText(jsilConfig.scriptRoot, function (result, error) {
      if ((result !== null) && (!error)) {
        var finisher = function () {
          $jsilbrowserstate.allAssetNames.push(args.filename);
          allAssets[getAssetName(args.filename)] = JSON.parse(result);
        };

        args.onDoneLoading(finisher);
      } else {
        args.onError(error);
      }
    });
  }
};

function $makeXNBAssetLoader (key, typeName) {
  assetLoaders[key] = function (args) {
    args.loadBytes(jsilConfig.contentRoot, function (result, error) {
      if ((result !== null) && (!error)) {
        var finisher = function () {
          $jsilbrowserstate.allAssetNames.push(args.filename);
          var key = getAssetName(args.filename, false);
          var assetName = getAssetName(args.filename, true);
          var parsedTypeName = JSIL.ParseTypeName(typeName);    
          var type = JSIL.GetTypeInternal(parsedTypeName, JSIL.GlobalNamespace, true);
          allAssets[key] = JSIL.CreateInstanceOfType(type, [assetName, result]);
        };

        args.onDoneLoading(finisher); 
      } else {
        args.onError(error);
      }
    });
  };
};

function guessMimeType (url) {
  var lower = url.toLowerCase();

  if (lower.indexOf(".png") >= 0)
    return "image/png";

  if (lower.indexOf(".gif") >= 0)
    return "image/gif";

  if (
    (lower.indexOf(".jpg") >= 0) ||
    (lower.indexOf(".jpeg") >= 0)
  )
    return "image/jpeg";

  if (lower.indexOf(".mp3") >= 0)
    return "audio/mpeg";

  if (lower.indexOf(".ogg") >= 0)
    return "audio/ogg; codecs=vorbis";

  if (lower.indexOf(".wav") >= 0)
    return "audio/wav";

  return "application/octet-stream";
};

function loadImageCORSHack (e, args) {
  var sourceURL = jsilConfig.contentRoot + args.filename;
  // FIXME: Pass mime type through from original XHR somehow?
  var mimeType = guessMimeType(sourceURL);

  args.loadBytes(jsilConfig.contentRoot, function (result, error) {
    if ((result !== null) && (!error)) {
      var objectURL = null;
      try {
        objectURL = JSIL.GetObjectURLForBytes(result, mimeType);
      } catch (exc) {
        onError(exc);
        return;
      }

      var finisher = function () {
        $jsilbrowserstate.allAssetNames.push(args.filename);
        allAssets[getAssetName(args.filename)] = new HTML5ImageAsset(getAssetName(args.filename, true), e);
      };

      JSIL.Browser.RegisterOneShotEventListener(e, "error", true, args.onError);
      JSIL.Browser.RegisterOneShotEventListener(e, "load", true, args.onDoneLoading.bind(null, finisher));
      e.src = objectURL;
    } else {
      args.onError(error);
    }
  });
};

function initCORSHack () {
  hasCORSXhr = false;
  hasCORSImage = false;

  try {
    var xhr = new XMLHttpRequest();
    hasCORSXhr = xhr && ("withCredentials" in xhr);
  } catch (exc) {
  }

  try {
    var img = document.createElement("img");
    hasCORSImage = img && ("crossOrigin" in img);
  } catch (exc) {
  }
}

function initAssetLoaders () {
  JSIL.InitBlobBuilder();
  initCORSHack();
  initSoundLoader();

  $makeXNBAssetLoader("XNB", "RawXNBAsset");
  $makeXNBAssetLoader("SpriteFont", "SpriteFontAsset");
  $makeXNBAssetLoader("Texture2D", "Texture2DAsset");
};