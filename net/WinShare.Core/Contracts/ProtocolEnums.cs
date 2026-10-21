// 本文件 DTO 字段名与 electron/types.ts 保持一致（camelCase），便于移植子代理对账。
namespace WinShare.Core.Contracts;

/// <summary>协议类型（TS: types.ts Protocol，'smb' | 'nfs' | 'ftp' | 'webdav'）。</summary>
public enum Protocol { Smb, Nfs, Ftp, Webdav }

/// <summary>共享缓存模式（TS: CreateShareOpts.cachingMode）。</summary>
public enum CachingMode { None, Manual, Documents, Programs, BranchCache }

/// <summary>访问枚举模式（TS: CreateShareOpts.folderEnumerationMode）。</summary>
public enum FolderEnumerationMode { AccessBased, Unrestricted }

/// <summary>SSL 策略（TS: types.ts sslPolicy）。</summary>
public enum SslPolicy { SslAllow, SslRequire, SslRequireCredentials }

/// <summary>FTP 认证模式（TS: types.ts authMode）。</summary>
public enum FtpAuthMode { Anonymous, Basic, Windows }

/// <summary>NFS 权限（TS: types.ts nfsPermission）。</summary>
public enum NfsPermission { Ro, Rw }

/// <summary>NFS 认证方式（TS: types.ts authentication 数组元素）。</summary>
public enum NfsAuthMethod { Krb5, Krb5i, Krb5p, Sys }
