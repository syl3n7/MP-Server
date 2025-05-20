using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.Linq;

[Route("api/[controller]")]
[ApiController]
public class ModelUploaderController : ControllerBase
{
    private readonly string _modelDirectory;

    public ModelUploaderController()
    {
        _modelDirectory = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "models");
        
        // Ensure the model directory exists
        if (!Directory.Exists(_modelDirectory))
        {
            Directory.CreateDirectory(_modelDirectory);
        }
    }

    [HttpGet("list")]
    public IActionResult ListModels()
    {
        var supportedExtensions = new[] { ".glb", ".gltf", ".obj", ".fbx" };
        
        var files = Directory.GetFiles(_modelDirectory)
            .Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLower()))
            .Select(f => new {
                name = Path.GetFileName(f),
                url = $"/models/{Path.GetFileName(f)}",
                size = new FileInfo(f).Length,
                lastModified = System.IO.File.GetLastWriteTime(f)
            })
            .ToList();
            
        return Ok(files);
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadModel(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No file uploaded" });
        }

        var supportedExtensions = new[] { ".glb", ".gltf", ".obj", ".fbx" };
        var fileExtension = Path.GetExtension(file.FileName).ToLower();
        
        if (!supportedExtensions.Contains(fileExtension))
        {
            return BadRequest(new { 
                error = "Unsupported file type. Supported types: " + string.Join(", ", supportedExtensions) 
            });
        }

        var filePath = Path.Combine(_modelDirectory, file.FileName);

        // Check if file already exists
        if (System.IO.File.Exists(filePath))
        {
            // Generate a unique filename
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file.FileName);
            var newFileName = $"{fileNameWithoutExt}_{DateTime.Now:yyyyMMddHHmmss}{fileExtension}";
            filePath = Path.Combine(_modelDirectory, newFileName);
        }

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        return Ok(new { 
            message = "File uploaded successfully", 
            url = $"/models/{Path.GetFileName(filePath)}" 
        });
    }

    [HttpDelete("{filename}")]
    public IActionResult DeleteModel(string filename)
    {
        var filePath = Path.Combine(_modelDirectory, filename);
        
        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = "File not found" });
        }

        try
        {
            System.IO.File.Delete(filePath);
            return Ok(new { message = "File deleted successfully" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to delete file: {ex.Message}" });
        }
    }
}