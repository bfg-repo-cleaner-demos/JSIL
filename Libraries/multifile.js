/* MultiFile - A JavaScript library to load multiple files from 
   tar archives and json_packed files (see http://gist.github.com/407595)

Copyright (c) 2010 Ilmari Heikkinen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

Updated 6-2013 by K. Gadd (kg@luminance.org):
  Fixed broken length parsing in headers
  Changed header.data to a byte array instead of a string
  Removed JSON parsing support
  Added onerror argument to .load()/.stream()

*/

MultiFile = function(){};

// Load and parse archive, calls onload after loading all files.
MultiFile.load = function(url, onload, onerror) {
  var o = new MultiFile();
  o.onload = onload;
  if (onerror)
    o.onerror = onerror;
  o.load(url);
  return o;
}

// Streams an archive from the given url, calling onstream after loading each file in archive.
// Calls onload after loading all files.
MultiFile.stream = function(url, onstream, onload, onerror) {
  var o = new MultiFile();
  o.onload = onload;
  o.onstream = onstream;
  if (onerror)
    o.onerror = onerror;
  o.load(url);
  return o;
}
MultiFile.prototype = {
  onerror : null,
  onload : null,
  onstream : null,
  pendingLongLink : null,
  
  load : function(url) {
    var xhr = new XMLHttpRequest();
    var self = this;
    var offset = 0;
    this.files = [];
    var isTar = (/\.tar(\?.*)?$/i).test(url);

    xhr.onreadystatechange = function() {
      if (xhr.readyState == 4) {
        if (xhr.status == 200 || xhr.status == 0) {
          if (isTar) {
            offset = self.processTarChunks(xhr.responseText, offset);

            if (self.onload)
              self.onload(xhr);
          } else
            self.onerror("File is not a TAR file");
        } else {
          if (self.onerror)
            self.onerror(xhr);
        }
      } else if (xhr.readyState == 3) {
        if (xhr.status == 200 || xhr.status == 0) {
          if (isTar)
            offset = self.processTarChunks(xhr.responseText, offset);
        }
      }
    };
    xhr.open("GET", url, true);
    xhr.overrideMimeType("text/plain; charset=x-user-defined");
    xhr.setRequestHeader("Content-Type", "text/plain");
    xhr.send(null);
  },
 
  onerror : function(xhr) {
    alert("Error: "+xhr.status);
  },
  
  cleanHighByte : function(s) {
    return s.replace(/./g, function(m) { 
      return String.fromCharCode(m.charCodeAt(0) & 0xff);
    });
  },
  
  parseTar : function(text) {
    this.files = [];
    this.processTarChunks(text, 0);
  },
  processTarChunks : function (responseText, offset) {
    while (responseText.length >= offset + 512) {
      var header = this.files.length == 0 ? null : this.files[this.files.length-1];

      if (header && (header.source === null)) {
        if (offset + header.length <= responseText.length) {
          header.source = responseText;
          header.sourceOffset = offset;

          if (header.fileType === "L") {
            this.pendingLongLink = header.getText();

            // The name in the longlink always has a trailing null
            var firstNull = this.pendingLongLink.indexOf(String.fromCharCode(0));
            if (firstNull)
              this.pendingLongLink = this.pendingLongLink.substring(0, firstNull);
          } else {
            if (this.onstream) 
              this.onstream(header);
          }

          offset += 512 * Math.ceil(header.length / 512);
        } else { // not loaded yet
          break;
        }

      } else {
        var header = this.parseTarHeader(responseText, offset);
        if (header.length > 0 || header.filename != '') {
          this.files.push(header);
          offset += 512;
          header.offset = offset;
        } else { // empty header, stop processing
          offset = responseText.length;
        }
      }
    }

    return offset;
  },
  parseTarHeader : function(text, offset) {
    return new TarFileEntry(this, text, offset);
  },
  parseTarNumber : function(text) {
    if (text.charCodeAt(0) & 0x80 == 1) {
      // GNU tar 8-byte binary big-endian number
      throw new Error("GNU binary big-endian numbers not implemented");
    } else {
      // :| This is OCTAL, not decimal...
      var result = parseInt('0'+text.replace(/[^\d]/g, ''), 8);
      if (isNaN(result) || (result < 0))
        throw new Error("TAR header parse error");

      return result;
    }
  },
}

TarFileEntry = function (tarFile, text, offset) {
  this.tarFile = tarFile;

  var i = offset || 0;

  this.filename = text.substring(i, i+=100).split("\0", 1)[0];
  this.mode = text.substring(i, i+=8).split("\0", 1)[0];
  this.uid = text.substring(i, i+=8).split("\0", 1)[0];
  this.gid = text.substring(i, i+=8).split("\0", 1)[0];
  this.length = tarFile.parseTarNumber(text.substring(i, i+=12));
  this.lastModified = text.substring(i, i+=12).split("\0", 1)[0];
  this.checkSum = text.substring(i, i+=8).split("\0", 1)[0];
  this.fileType = text.substring(i, i+=1).split("\0", 1)[0];
  this.linkName = text.substring(i, i+=100).split("\0", 1)[0];

  this.sourceOffset = null;
  this.source = null;

  if (tarFile.pendingLongLink) {
    if (tarFile.pendingLongLink.indexOf(this.filename) !== 0)
      throw new Error("Invalid long tar filename");

    this.filename = tarFile.pendingLongLink;
    tarFile.pendingLongLink = null;
  }
};

TarFileEntry.prototype = Object.create(Object.prototype);

TarFileEntry.prototype.getBytes = function () {
  var result;
  if (typeof (window.Uint8Array) !== "undefined") {
    result = new Uint8Array(this.length);
  } else {
    result = new Array(this.length);
  }

  var offset = this.sourceOffset | 0;
  var text = this.source;

  for (var i = 0, l = this.length | 0; i < l; i = (i + 1) | 0)
    result[i] = text.charCodeAt((i + offset) | 0) & 0xFF;

  return result;
};

TarFileEntry.prototype.getText = function () {
  return this.source.substring(this.sourceOffset, this.sourceOffset + this.length)
};

TarFileEntry.prototype.toString = function () {
  return "'" + this.filename + "' (" + this.length + " byte(s))";
};