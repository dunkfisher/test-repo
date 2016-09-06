﻿using IOMG.Umbraco.StandaloneServices;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Umbraco.Core;
using Umbraco.Core.Models;
using Umbraco.Core.Services;

namespace Website.ContentAdmin
{
    class Program
    {
        private static string _beerImagesRootDirectory;
        private static Regex _dateTakenRegex = new Regex(":");
        
        private static string BeerImagesRootDirectory
        {
            get
            {
                if (_beerImagesRootDirectory == null)
                {
                    _beerImagesRootDirectory = ConfigurationManager.AppSettings["BeerImagesRootDirectory"];
                }
                return _beerImagesRootDirectory;
            }
        }

        static void Main(string[] args)
        {
            RunUmbraco();
        }

        private static void RunUmbraco()
        {
            Console.Title = "Umbraco Console";

            var umbracoAccess = new ServiceAccess();

            //Exit the application?
            var waitOrBreak = true;
            while (waitOrBreak)
            {
                //List options
                Console.WriteLine("-- Options --");
                Console.WriteLine("Load beers from file: b");
                Console.WriteLine("Update image (tasted) date: i");
                Console.WriteLine("Quit application: q");

                var input = Console.ReadLine();
                if (string.IsNullOrEmpty(input) == false && input.ToLowerInvariant().Equals("q"))
                    waitOrBreak = false;                
                else if (string.IsNullOrEmpty(input) == false && input.ToLowerInvariant().Equals("b"))
                    UploadBeersFromFile(umbracoAccess.Services.ContentService, umbracoAccess.Services.MediaService);
                else if (string.IsNullOrEmpty(input) == false && input.ToLowerInvariant().Equals("i"))
                    UpdateBeerImageDate(umbracoAccess.Services.ContentTypeService, umbracoAccess.Services.ContentService, umbracoAccess.Services.MediaService);
            }
        }

        private static void UploadBeersFromFile(IContentService contentService, IMediaService mediaService)
        {
            // Indices of columns
            var nameIndex = -1;
            var countryIndex = -1;
            var brewerIndex = -1;
            var notesIndex = -1;
            var ratingIndex = -1;
            var imageCandidateIndex = -1;
            var imageChosenIndex = -1;

            // Values of columns
            string name = null;
            string country = null;
            string brewer = null;
            string notes = null;
            short rating = 0;
            string imageCandidates = null;
            string imageChosen = null;
            int imageId = 0;
            DateTime imageDateTaken = DateTime.MinValue;

            var inputFileDirectory = ConfigurationManager.AppSettings["BeerFileDirectory"];
            Console.WriteLine("Please enter name of the input file:");
            var inputFileName = Console.ReadLine();
            Console.WriteLine();
            var inputFilePath = Path.Combine(inputFileDirectory, inputFileName);
            if (!System.IO.File.Exists(inputFilePath))
            {
                Console.WriteLine("Input file " + inputFilePath + " doesn't exist.");
                Console.ReadLine();
                return;
            }

            using (var fileReader = new StreamReader(inputFilePath))
            {
                var currentRow = new string[0];
                var firstRow = true;
                while (!fileReader.EndOfStream)
                {
                    currentRow = fileReader.ReadLine().Split(',');
                    if (firstRow)
                    {
                        nameIndex = currentRow.IndexOf("Beer");
                        countryIndex = currentRow.IndexOf("Country");
                        brewerIndex = currentRow.IndexOf("Brewer");
                        notesIndex = currentRow.IndexOf("Notes");
                        ratingIndex = currentRow.IndexOf("Rating");
                        imageCandidateIndex = currentRow.IndexOf("Matched Images");
                        imageChosenIndex = currentRow.IndexOf("Used Image");

                        if (new[] { nameIndex, countryIndex, brewerIndex, notesIndex, ratingIndex, imageCandidateIndex, imageChosenIndex }.Any(x => x < 0))
                        {
                            Console.WriteLine("One or more columns are missing from the input file.");
                            Console.ReadLine();
                            return;
                        }

                        firstRow = false;
                        continue;
                    }

                    name = currentRow[nameIndex];
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Console.WriteLine("Missing beer name.");
                        Console.WriteLine();
                        continue;
                    }

                    Console.WriteLine(string.Format("Loading data for {0}", name, country));

                    country = currentRow[countryIndex];
                    if (string.IsNullOrWhiteSpace(country))
                    {
                        Console.WriteLine("Missing country of origin.");
                        Console.WriteLine();
                        continue;
                    }

                    Console.WriteLine(string.Format("Country of origin: " + country));

                    brewer = currentRow[brewerIndex];
                    Console.WriteLine("No brewer specified.");

                    notes = currentRow[notesIndex];

                    if (!short.TryParse(currentRow[ratingIndex], out rating))
                    {
                        Console.WriteLine("No rating given.");
                    }

                    imageCandidates = currentRow[imageCandidateIndex];
                    imageChosen = currentRow[imageChosenIndex];

                    if (string.IsNullOrWhiteSpace(imageChosen))
                    {
                        Console.WriteLine("No image specified. Searching..");
                        imageChosen = FindImage(name, country, out imageCandidates);
                    }

                    if (!string.IsNullOrWhiteSpace(imageChosen))
                    {
                        Console.WriteLine("Uploading image: " + imageChosen + "..");
                        imageId = UploadImage(imageChosen, country, mediaService);
                        if (imageId >= 0)
                        {
                            var imageChosenPath = Path.Combine(BeerImagesRootDirectory, country, Path.ChangeExtension(imageChosen, ".jpg"));
                            imageDateTaken = GetDateTakenFromImage(imageChosenPath);
                        }
                    }
                    else
                    {
                        Console.WriteLine("No matching image was found.");
                    }

                    Console.WriteLine("Proceeding to upload beer to CMS..");
                    var beerId = UploadBeer(name, brewer, country, notes, rating, imageId, imageDateTaken, contentService);
                    if (beerId >= 0)
                    {
                        Console.WriteLine("Beer successfully uploaded.");
                    }
                    else
                    {
                        Console.WriteLine("Couldn't upload beer.");
                    }
                    Console.WriteLine();

                    // TODO: create output file as input file but with image columns updated
                }
            }

            // TODO: sort beers for each country
        }

        private static void UpdateBeerImageDate(IContentTypeService contentTypeService, IContentService contentService, IMediaService mediaService)
        {
            var beerContentType = contentTypeService.GetContentType("Beer");
            foreach (var beer in contentService.GetContentOfContentType(beerContentType.Id))
            {
                Console.WriteLine();
                Console.WriteLine("Updating image date for " + beer.Name + "..");

                var country = beer.Parent();
                if (country == null)
                {
                    Console.WriteLine("Parent node not found.");
                    continue;
                }

                var mediaId = beer.Properties["image"].Value;
                if (mediaId == null)
                {
                    Console.WriteLine("No image associated with beer.");
                    continue;
                }

                var media = mediaService.GetById(Convert.ToInt32(mediaId));
                if (media == null)
                {
                    Console.WriteLine("Image media of id " + mediaId + " is missing.");
                    continue;
                }

                var mediaFilePath = media.Properties["umbracoFile"].Value;
                if (mediaFilePath == null)
                {
                    Console.WriteLine("Image media path is missing.");
                    continue;
                }

                var imageFileName = Path.GetFileName(mediaFilePath.ToString());
                var imageFilePath = Path.Combine(BeerImagesRootDirectory, country.Name, imageFileName);
                if (!System.IO.File.Exists(imageFilePath))
                {
                    Console.WriteLine("Image file " + imageFilePath + " doesn't exist.");
                    continue;
                }

                var imageDate = GetDateTakenFromImage(imageFilePath);
                var beerImageDate = beer.Properties["imageDate"].Value;
                Console.WriteLine("Image date taken: " + imageDate);
                Console.WriteLine("Date in CMS: " + beerImageDate ?? "Unspecified");
                if (imageDate != DateTime.MinValue && (beerImageDate == null || Convert.ToDateTime(beerImageDate) != imageDate))
                {
                    beer.Properties["imageDate"].Value = imageDate;
                    contentService.Save(beer);
                    Console.WriteLine("Image date updated in CMS.");
                }
            }
        }

        private static string FindImage(string beerName, string country, out string candidates)
        {
            var imageDirectory = BeerImagesRootDirectory;
            var imageCountryPath = Path.Combine(imageDirectory, country);
            if (!Directory.Exists(imageCountryPath))
            {
                Console.WriteLine("Images folder " + imageCountryPath + " doesn't exist.");
            }

            var possibleMatches = new SortedDictionary<int, List<string>>();
            foreach (var filePath in Directory.GetFiles(imageCountryPath))
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var beerNameMatchArray = beerName.ToLower().Split(' ');
                var fileNameMatchArray = fileName.ToLower().Split('-');
                var matchingWords = beerNameMatchArray.Intersect(fileNameMatchArray).Count();
                var difference = Math.Abs(beerNameMatchArray.Count() - matchingWords);
                if (matchingWords > 0)
                {
                    if (possibleMatches.ContainsKey(difference))
                    {
                        possibleMatches[difference].Add(fileName);
                    }
                    else
                    {
                        possibleMatches.Add(difference, new List<string>(new[] { fileName }));
                    }
                }                
            }
            candidates = string.Join(",", possibleMatches.SelectMany(x => x.Value));
            return possibleMatches.Count() > 0 ? possibleMatches.First().Value[0] : null;
        }

        private static int UploadImage(string imageName, string country, IMediaService mediaService)
        {
            var imageDirectory = BeerImagesRootDirectory;
            var imageFileName = Path.ChangeExtension(imageName, ".jpg");
            var imagePath = Path.Combine(imageDirectory, country, imageFileName);
            if (!System.IO.File.Exists(imagePath))
            {
                Console.WriteLine("Image file " + imagePath + " doesn't exist.");
                return -1;
            }

            using (var fileStream = new FileStream(imagePath, FileMode.Open))
            {
                var beerMediaRoot = mediaService.GetRootMedia().SingleOrDefault(x => x.Name == "Beers");
                if (beerMediaRoot == null)
                {
                    Console.WriteLine("Couldn't find media folder with name \"Beers\".");
                    return -1;
                }

                var countryMediaParent = mediaService.GetChildren(beerMediaRoot.Id).SingleOrDefault(x => x.Name == country);
                if (countryMediaParent == null)
                {
                    Console.WriteLine("Couldn't find media folder with name " + country + ".");
                    return -1;
                }

                // Check existence of media
                var existingMedia = mediaService.GetChildren(countryMediaParent.Id).SingleOrDefault(x => x.Name.Equals(imageFileName, StringComparison.CurrentCultureIgnoreCase));
                if (existingMedia != null)
                {
                    Console.WriteLine("Image media " + imageFileName + " already exists.");
                    return existingMedia.Id;
                }

                var image = mediaService.CreateMedia(imageFileName, countryMediaParent, "Image");
                image.SetValue("umbracoFile", Path.GetFileName(fileStream.Name), fileStream);
                mediaService.Save(image);
                return image.Id;
            }
        }

        private static int UploadBeer(string name, string brewer, string country, string notes, short rating, int imageId, DateTime imageDateTaken, IContentService contentService)
        {
            var rootContent = contentService.GetRootContent().SingleOrDefault();
            var beersRoot = contentService.GetChildren(rootContent.Id).SingleOrDefault(x => x.Name == "Beer Reviews");
            if (beersRoot == null)
            {
                Console.WriteLine("Couldn't find node with name \"Beer Reviews\".");
                return -1;
            }

            var countryItem = contentService.GetChildren(beersRoot.Id).SingleOrDefault(x => x.Name == country);
            if (countryItem == null)
            {
                Console.WriteLine("Couldn't find country node with name " + country + ".");
                return -1;
            }

            // Check existence of content
            var existingBeer = contentService.GetChildren(countryItem.Id).SingleOrDefault(x => x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            if (existingBeer != null)
            {
                Console.WriteLine("Beer " + name + " already exists.");
                return existingBeer.Id;
            }

            var newBeer = contentService.CreateContent(name, countryItem.Id, "Beer");
            newBeer.Properties["fullName"].Value = name;
            newBeer.Properties["brewer"].Value = brewer;
            newBeer.Properties["image"].Value = imageId;
            newBeer.Properties["imageDate"].Value = imageDateTaken;
            newBeer.Properties["review"].Value = notes;
            newBeer.Properties["rating"].Value = rating;

            //Save the Content
            contentService.Save(newBeer);
            return newBeer.Id;
        }

        private static DateTime GetDateTakenFromImage(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    using (Image myImage = Image.FromStream(fs, false, false))
                    {
                        PropertyItem propItem = myImage.GetPropertyItem(36867);
                        string dateTakenText = _dateTakenRegex.Replace(Encoding.UTF8.GetString(propItem.Value), "-", 2);
                        DateTime dateTaken;
                        DateTime.TryParse(dateTakenText, out dateTaken);
                        return dateTaken;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error when getting date taken from image: " + e.Message);
                return DateTime.MinValue;
            }
        }
    }
}