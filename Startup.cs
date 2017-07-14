using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;

using CommonMark;
using DotLiquid;
using YamlDotNet;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace DotNetBlog
{
	public class Startup
	{
		string postsDir = Path.Combine(Directory.GetCurrentDirectory(), "_posts");

		string siteDir = Path.Combine(Directory.GetCurrentDirectory(), "_site");

		string layoutsDir = Path.Combine(Directory.GetCurrentDirectory(), "_layouts");

		Dictionary<string, object> site = null;
		
		public void ConfigureServices(IServiceCollection services)
		{
		    services.AddRouting();
		}

		private string elem(HttpContext context, string key)
		{
			var s = context.GetRouteValue(key) as string;
			if (string.IsNullOrEmpty(s))
			{
				return "*";
			}
			return s.ToString();
		}

		private string makeUrl(HttpContext context)
		{
			var year = context.GetRouteValue("year") as string;
			var month = context.GetRouteValue("month") as string;
			var day = context.GetRouteValue("day") as string;
			if (string.IsNullOrEmpty(year))
				return "";
			if (string.IsNullOrEmpty(month))
				return year;
			if (string.IsNullOrEmpty(day))
				return year + "/" + month;
			return year + "/" + month + "/" + day;
		}

		private object makeEntry(string filename)
		{
			var content = File.ReadAllText(filename);
			var title = Path.GetFileNameWithoutExtension(filename).Split(new Char[]{'-'}, 4)[3].Replace("_", " ");
			DateTime updatedAt = DateTime.Parse(string.Join("/", Path.GetFileName(filename).Split(new Char[]{'-'}, 4).Take(3)));
			if (content.StartsWith("---\n"))
			{
				var pos = content.IndexOf("\n---\n");
				if (pos > 0)
				{
					var header = content.Substring(0, pos);
					content = content.Substring(pos + 4);
					YamlStream yaml = new YamlStream();
					yaml.Load(new StringReader(header));
					((YamlMappingNode)yaml.Documents[0].RootNode).Children.ToList().ForEach(x =>
					{
						switch (x.Key.ToString())
						{
							case "title":
								title = x.Value.ToString();
								break;
							case "date":
								DateTime.TryParse(x.Value.ToString(), out updatedAt);
								break;
						}
					});
				}
			}
			return new {
				title = title,
				content = CommonMarkConverter.Convert(content),
				url = "/" + string.Join("/", filename.Substring(postsDir.Length + 1, filename.Length - postsDir.Length - 4).Split(new Char[]{'-'}, 4)),
				updatedAt = updatedAt
			};
		}

		private Task handleError(HttpContext context, int statusCode, string statusMsg)
		{
			context.Response.StatusCode = statusCode;
			context.Response.ContentType = "text/html; charset=utf-8";
			var file = Path.Combine(siteDir, statusCode + ".html");
			if (File.Exists(file))
			{
				return context.Response.WriteAsync(
					Template.Parse(File.ReadAllText(file))
						.Render(Hash.FromAnonymousObject(new {
							site = site,
							page = new {
								title = makeUrl(context),
								url = "/" + makeUrl(context)
							},
						}))
				);
			}
			return context.Response.WriteAsync(statusMsg);
		}

		private Task handleFiles(HttpContext context, string page = null)
		{
			if (page == null)
			{
				page = Path.Combine(layoutsDir, "default.html");
			}
			var mask = string.Format("{0}-{1}-{2}-*.md",
					elem(context, "year"),
					elem(context, "month"),
					elem(context, "day"));
			var files = Directory.GetFiles(postsDir, mask, System.IO.SearchOption.AllDirectories);
			if (files.Count() == 0)
			{
				return handleError(context, 404, "Not Found");
			}
			var posts = files.OrderByDescending(File.GetLastWriteTime).Select(makeEntry);
			var contextType = "application/octet-stream";
			var ct = new FileExtensionContentTypeProvider().TryGetContentType(page, out contextType);
			context.Response.ContentType = contextType;
			return context.Response.WriteAsync(
				Template.Parse(File.ReadAllText(page))
					.Render(Hash.FromAnonymousObject(new {
						site = site,
						page = new {
							title = makeUrl(context),
							url = "/" + makeUrl(context)
						},
						posts = posts
					}))
			);
		}
		private Task handleFeed(HttpContext context)
		{
			var mask = string.Format("{0}-{1}-{2}-*.md",
					elem(context, "year"),
					elem(context, "month"),
					elem(context, "day"));
			var files = Directory.GetFiles(postsDir, mask, System.IO.SearchOption.AllDirectories);
			if (files.Count() == 0)
			{
				return handleError(context, 404, "Not Found");
			}
			var posts = files.OrderByDescending(File.GetLastWriteTime).Select(makeEntry);
			context.Response.ContentType = "application/rss+xml; charset=utf-8";
			return context.Response.WriteAsync(
				Template.Parse(File.ReadAllText(Path.Combine(siteDir, "feed.rss")))
					.Render(Hash.FromAnonymousObject(new {
						site = site,
						page = new {
							title = makeUrl(context),
							url = "/" + makeUrl(context)
						},
						posts = posts
					}))
			);
		}

		public Task handleEntry(HttpContext context)
		{
			var file = string.Format("{0}-{1}-{2}-{3}.md",
					context.GetRouteValue("year"),
					context.GetRouteValue("month"),
					context.GetRouteValue("day"),
					context.GetRouteValue("slug"));
			context.Response.ContentType = "text/html; charset=utf-8";
			try
			{
				var post = makeEntry(Path.Combine(postsDir, file));
				return context.Response.WriteAsync(
					Template.Parse(File.ReadAllText(Path.Combine(layoutsDir, "post.html")))
						.Render(Hash.FromAnonymousObject(new {
							site = site,
							page = post,
							post = post
						}))
				);			
			}
			catch (FileNotFoundException)
			{
				return handleError(context, 404, "Not Found");
			}
			catch (Exception)
			{
				return handleError(context, 500, "Internal Server Error");
			}
		}

		private bool doCmd(string cmdline)
		{
			ProcessStartInfo ps = new ProcessStartInfo();
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				ps.FileName = Environment.GetEnvironmentVariable("comspec");
				ps.Arguments = string.Format("/c \"{0}\"", cmdline);
			}
			else
			{
				ps.FileName = Environment.GetEnvironmentVariable("SHELL");
				ps.Arguments = string.Format("-c '{0}'", cmdline);
			}
			ps.WorkingDirectory = Path.Combine(Directory.GetCurrentDirectory());
			ps.CreateNoWindow = true;
			ps.UseShellExecute = false;
			ps.RedirectStandardOutput = true;
			using (Process cmd = new Process())
			{
				cmd.StartInfo = ps;
				cmd.Start();
				cmd.WaitForExit();
				return cmd.ExitCode == 0;
			}
		}

		void removeDir(string targetPath, bool removeRoot = false)
		{
			var di = new DirectoryInfo(targetPath);
			if (!di.Exists)
			{
				return;
			}
			foreach (FileInfo file in di.GetFiles())
			{
				if (file.Name != ".gitkeep")
				{
					file.Delete(); 
				}
			}
			if (removeRoot)
			{
				di.Delete(true);
			}
			else
			{
				foreach (DirectoryInfo dir in di.GetDirectories())
				{
					dir.Delete(true); 
				}
			}
		}

		void copyDir(string sourcePath, string destinationPath)
		{
			foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
			{
				Directory.CreateDirectory(dirPath.Replace(sourcePath, destinationPath));
			}
			foreach (string newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
			{
				File.Copy(newPath, newPath.Replace(sourcePath, destinationPath), true);
			}
		}

		private Task pullFiles(HttpContext context)
		{
			var cloneDir = Path.Combine(Path.GetTempPath(), "dotnet-blog-posts");
			removeDir(cloneDir, true);
			if (!doCmd(string.Format("git clone {0} {1}", site["clone-url"], cloneDir)))
			{
				return context.Response.WriteAsync("NG");
			}
			removeDir(postsDir);
			removeDir(layoutsDir);
			removeDir(siteDir);
			copyDir(Path.Combine(cloneDir, "_posts"), postsDir);
			copyDir(Path.Combine(cloneDir, "_layouts"), layoutsDir);
			copyDir(Path.Combine(cloneDir, "_site"), siteDir);
			removeDir(cloneDir, true);
			return context.Response.WriteAsync("OK");
		}

		public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
		{
			loggerFactory.AddConsole();

			using (var stream = new FileStream(@"_config.yml", FileMode.Open))
			using (var reader = new StreamReader(stream))
			{
				site = new Deserializer().Deserialize<Dictionary<string, object>>(reader);
			}

			if (env.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
			}
			app.UseStaticFiles(new StaticFileOptions
			{
				FileProvider = new PhysicalFileProvider(siteDir),
				RequestPath = new PathString("")
			});

			var routeBuilder = new RouteBuilder(app);
			routeBuilder.MapGet("{file?}", context =>
			{
				var page = context.Request.Path.Value;
				if (page == "/")
				{
					page = "index.html";
				}
				return handleFiles(context, Path.Combine(siteDir, page.TrimStart(new Char[]{'/'})));
			});
			routeBuilder.MapPost("pull", context => pullFiles(context));
			routeBuilder.MapGet("{year:int}/", context => handleFiles(context));
			routeBuilder.MapGet("{year:int}/{month:int}/", context => handleFiles(context));
			routeBuilder.MapGet("{year:int}/{month:int}/{day:int}/", context => handleFiles(context));
			routeBuilder.MapGet("{year:int}/{month:int}/{day:int}/{slug}", context => handleEntry(context));

			app.UseRouter(routeBuilder.Build());
		}
	}
}
