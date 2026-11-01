namespace Nexus.Tests.Modules;

/// <summary>Amostras reais dos serviços oficiais (gravadas em 01/11/2026 pela ferramenta de desenvolvimento; os testes não usam rede).</summary>
internal static class DriverUpdatesVendorFixtures
{
    /// <summary>Resposta REAL do DriverManualLookup (psid=115, pfid=898, osID=135, languageCode=1046), obtida em 01/11/2026.</summary>
    public const string NvidiaLookupGtx1650NotebookWin11 = """"
{ "Success" : "1", "IDS" : [{ "downloadInfo": { "Success" : "1", "ID" : "282471", "DownloadTypeID" : "1", "DownloadStatusID" : "3", "Name" : "Driver%20GeForce%20Game%20Ready", "NameLocalized" : "Driver%20GeForce%20Game%20Ready", "ShortDescription" : "Driver%20GeForce%20Game%20Ready", "DeviceToProductFamilyName" : "", "Release" : "620", "Version" : "621.01", "DisplayVersion" : "", "GFE_DisplayVersion" : "11.0.9.565", "CDKitUSBEmitterDriverVersion" : "", "CDKitGPUDriverVersion" : "", "IsBeta" : "0", "IsWHQL" : "1", "IsRecommended" : "0", "IsFeaturePreview" : "0", "IsNewest" : "0", "IsDC" : "1", "IsCRD" : "0", "HasNetInst" : "0", "CudaToolkitVersion" : "", "IsArchive" : "0", "IsActive" : "1", "IsEmailRequired" : "0", "ReleaseDateTime" : "2026 outubro 21", "DetailsURL" : "https://www.nvidia.com/pt-br/drivers/details/282471/", "DownloadURL" : "https://us.download.nvidia.com/Windows/621.01/621.01-notebook-win10-win11-64bit-international-dch-whql.exe", "EMAN_REVRES_BD" : "91-lqssm-lswfg-a2wsu-drp", "EMITR_BD" : "memCache:1%20rTime:3%20sTime:1", "DownloadURLFileSize" : "1.04 GB", "BannerURL" : "https://www.nvidia.com/pt-br/geforce/billboards/displaydriver/standalone/eve-std-game-ready-driver-2026-banners/", "BannerURLGfe" : "https://www.nvidia.com/pt-br/geforce/billboards/displaydriver/gfe/eve-gfe-game-ready-driver-2026-banners/", "ReleaseNotes":"%09Although%20GeForce%20Game%20Ready%20Drivers%20and%20NVIDIA%20Studio%20Drivers%20can%20be%20installed%20on%20supported%20notebook%20GPUs%2C%20the%20original%20equipment%20manufacturer%20%28OEM%29%20provides%20certified%20drivers%20for%20your%20specific%20notebook%20on%20their%20website.%20NVIDIA%20recommends%20that%20you%20check%20with%20your%20notebook%20OEM%20for%20recommended%20software%20updates%20for%20your%20notebook.%3Cbr%20/%3E%20%3Cbr%20/%3E%20%3Cb%3EGame%20Ready%20for%20Phantom%20Blade%20Zero%20%26amp%3B%20Call%20of%20Duty%3A%20Modern%20Warfare%204%3C/b%3E%3Cbr%20/%3EThis%20new%20Game%20Ready%20Driver%20provides%20the%20best%20gaming%20experience%20for%20the%20latest%20new%20games%20and%20updates%20supporting%20DLSS%20and%20RTX%20technologies%20including%20Phantom%20Blade%20Zero%2C%20Call%20of%20Duty%3A%20Modern%20Warfare%204%2C%20NBA%202K27%2C%20Cyberpunk%202077%2C%20and%20Crimson%20Desert%20Enhanced%3A%20Charting%20the%20Unknown.%3Cbr%20/%3E%20%20%3Cbr%20/%3E%20%20%3Cb%3EFixed%20Gaming%20Bugs%3C/b%3E%3Cbr%20/%3E%20%20%3Cul%3E%3Cli%3EWuthering%20Waves%3A%20May%20crash%20during%20gameplay%20when%20using%20NVIDIA%20Freestyle%20filters%20with%20DLSS%20Frame%20Generation%20enabled.%20%5B5747079%5D%3C/li%3E%20%3Cli%3EStar%20Citizen%3A%20Resolves%20a%20crash%20that%20may%20be%20seen%20when%20using%20Vulkan%20API%20%5B6680590%5D%3C/li%3E%20%3Cli%3ESurviving%20Mars%3A%20Relaunched%20may%20crash%20during%20gameplay%20after%20updating%20to%20R595%20drivers%20%5B5965452%5D%3C/li%3E%3C/ul%3E%20%20%20%20%20%20%3Cbr%20/%3E%20%20%20%20%20%20%3Cb%3EFixed%20General%20Bugs%3C/b%3E%3Cbr%20/%3E%20%20%20%20%20%20%3Cul%3E%3Cli%3EQHD%20monitor%20running%20at%20360%20Hz%20may%20have%20occasional%20black%20flash%20during%20gameplay%20%5B4846825%5D%3C/li%3E%20%3Cli%3EWhen%20gaming%20on%20a%20360Hz%20G-SYNC%20Pulsar%20monitor%2C%20FPS%20may%20drop%20to%20320FPS%20%5B6408986%5D%3C/li%3E%20%3Cli%3EMeta%20Horizon%20Link%20stream%20may%20freeze%20when%20connected%20over%20USB%20%5B6292679%5D%3C/li%3E%20%3Cli%3EIntermittent%20dimming%20may%20be%20observed%20on%20certain%20Asus%20notebooks%20after%20updating%20to%20R620%20drivers%20%5B6807011%5D%3C/li%3E%3C/ul%3E%20%20%20%20%20%20%20%20%20%20%3Cbr%20/%3E%20%20%20%20%20%20%20%20%20%20%3Ca%20href%3D%22https%3A//www.nvidia.com/en-us/geforce/news/phantom-blade-zero-nvidia-geforce-game-ready-driver%22%3ELearn%20more%20in%20our%20Game%20Ready%20Driver%20article%20here%3C/a%3E.%20%20%3Cbr%20/%3E%20%20%20%20%20%20%20%20%20%3Cbr%20/%3E%20%20%20%20%20%20%20%20%20%3Cp%20align%3D%22justify%22%3E%3Cimg%20title%3D%22Game%20Ready%20Driver%22%20src%3D%22https%3A//images.nvidia.com/content/GRD/R615GA1/nvidia-geforce-game-ready-drivers-gfe-grd-1144x298-banner.jpg%22%20alt%3D%22Game%20Ready%20Driver%22%20width%3D%22100%25%22%20/%3E%3C/p%3E%09","OtherNotes":"%09Effective%20October%202021%2C%20Game%20Ready%20Driver%20upgrades%2C%20including%20performance%20enhancements%2C%20new%20features%2C%20and%20bug%20fixes%2C%20will%20be%20available%20for%20systems%20utilizing%20Maxwell%2C%20Pascal%2C%20Turing%2C%20and%20Ampere-series%20GPUs.%20Critical%20security%20updates%20will%20be%20available%20on%20systems%20utilizing%20desktop%20Kepler-series%20GPUs%20through%20September%202024.%20A%20complete%20list%20of%20desktop%20Kepler-series%20GeForce%20GPUs%20can%20be%20found%20%3Ca%20href%3D%22https%3A//nvidia.custhelp.com/app/answers/detail/a_id/5202/kw/kepler%2520support%22%3Ehere%3C/a%3E.%3Cbr%20/%3E%20%20%3Cbr%20/%3E%20%20Exceptions%3A%20%20%3Col%3E%20%20%20%20%3Cli%3ENotebooks%20supporting%20Hybrid%20Power%20technology%20are%20not%20supported%20%28NVIDIA%20Optimus%20technology%20is%20supported%29.%3C/li%3E%20%20%20%20%3Cli%3EThe%20following%20Sony%20VAIO%20notebooks%20are%20included%20in%20the%20Verde%20notebook%20program%3A%20Sony%20VAIO%20F%20Series%20with%20NVIDIA%20GeForce%20310M%2C%20GeForce%20GT%20330M%2C%20GeForce%20GT%20425M%2C%20GeForce%20GT%20520M%20or%20GeForce%20GT%20540M.%20Other%20Sony%20VAIO%20notebooks%20are%20not%20included%20%28please%20contact%20Sony%20for%20driver%20support%29.%3C/li%3E%20%20%20%20%3Cli%3EFujitsu%20notebooks%20are%20not%20included%20%28Fujitsu%20Siemens%20notebooks%20are%20included%29.%3C/li%3E%20%20%3C/ol%3E%20%20%3Cbr%20/%3E%20%20Release%20Notes%3A%3Cbr%20/%3E%20%20%20%3Cul%3E%20%20%20%20%20%3Cli%3E%3Ca%20target%3D%22_blank%22%20href%3D%22https%3A//us.download.nvidia.com/Windows/621.01/621.01-geforce-win11-x64-release-notes.pdf%22%20title%3D%22Game%20Ready%20Driver%20Release%20Notes%20v621.01%22%3EGame%20Ready%20Driver%20Release%20Notes%20%28v621.01%29%3C/a%3E%3C/li%3E%3C/ul%3E%09","InstallationNotes":"","Overview":"","LanguageName" : "Português%20(Brazil)", "OSName" : "Windows%2011", "OsCode" : "10.0", "OSList":[{"OSName" : "Windows%2010%2064-bit", "OsCode" : "10.0"},{"OSName" : "Windows%2011", "OsCode" : "10.0"}],"Is64Bit" : "1", "Messaging" : [{"MessageCode" : "DownloadIDDetailsFound", "MessageValue" : "The Driver DownloadID details found"}], "series" : [{"seriesname" : "GeForce%20RTX%2050%20Series%20(Notebooks)", "products" : [{"productName" : "NVIDIA%20GeForce%20RTX%205090%20Laptop%20GPU"},{"productName" : "NVIDIA%20GeForce%20RTX%205080%20Laptop%20GPU"},{"productName" : "NVIDIA%20GeForce%20RTX%205070%20Ti%20Laptop%20GPU"},{"productName" : "NVIDIA%20GeForce%20RTX%205070%20Laptop%20GPU"},{"productName" : "NVIDIA%20GeForce%20RTX%205060%20Laptop%20GPU"},{"productName" : "NVIDIA%20GeForce%20RTX%205050%20Laptop%20GPU"}]},{"seriesname" : "GeForce%20RTX%2040%20Series%20(Notebooks)", "products" : [{"productName" : "GeForce%20RTX%204090%20Laptop%20GPU"},{"productName" : "GeForce%20RTX%204080%20Laptop%20GPU"},{"productName" : "GeForce%20RTX%204070%20Laptop%20GPU"},{"productName" : "GeForce%20RTX%204060%20Laptop%20GPU"},{"productName" : "GeForce%20RTX%204050%20Laptop%20GPU"}]},{"seriesname" : "GeForce%20RTX%2030%20Series%20(Notebooks)", "products" : [{"productName" : "GeForce%20RTX%203080%20Ti%20Laptop%20GPU"},{"productName" : "GeForce%20RTX%203080%20Laptop%20GPU"},{"productName" : "GeForce%20RTX%203070%20Ti%20Laptop%20GPU"},{"productName" : "GeForce%20RTX%203070%20Laptop%20GPU"},{"productName" : "GeForce%20RTX%203060%20Laptop%20GPU"},{"productName" : "GeForce%20RTX%203050%20Ti%20Laptop%20GPU"},{"productName" : "GeForce%20RTX%203050%20Laptop%20GPU"}]},{"seriesname" : "GeForce%20RTX%2020%20Series%20(Notebooks)", "products" : [{"productName" : "GeForce%20RTX%202080%20SUPER"},{"productName" : "GeForce%20RTX%202080"},{"productName" : "GeForce%20RTX%202070%20SUPER"},{"productName" : "GeForce%20RTX%202070"},{"productName" : "GeForce%20RTX%202060"},{"productName" : "GeForce%20RTX%202050"}]},{"seriesname" : "GeForce%20MX500%20Series%20(Notebooks)", "products" : [{"productName" : "GeForce%20MX570"},{"productName" : "GeForce%20MX550"}]},{"seriesname" : "GeForce%20MX400%20Series%20(Notebooks)", "products" : [{"productName" : "GeForce%20MX450"}]},{"seriesname" : "GeForce%20GTX%2016%20Series%20(Notebooks)", "products" : [{"productName" : "GeForce%20GTX%201660%20Ti"},{"productName" : "GeForce%20GTX%201650%20Ti"},{"productName" : "GeForce%20GTX%201650"}]}]}}] ,"spcall" : "EXEC NDL_usp_Download_select_by_matrix_values null,898,115,null,135,null,0, null,1046,1,null,null,null,null,null,null,1,null,null,0,null,1,null,0,0,null,null,null,null,null,null,null,null,null,null,null,null,null,1,null", "Request" : [{"downloadID" : "null", "pfid" : "898", "psid" : "115", "deviceID" : "null", "osID" : "135", "osCode" : "null", "is64bit" : "0", "languageID" : "null", "languageCode" : "1046", "dltype" : "1", "isArchive" : "null", "beta" : "null", "includebeta" : "null", "gtversion" : "null", "version" : "null", "release" : "null", "isWHQL" : "1", "isNewest" : "null", "locationID" : "null", "sort1" : "0", "sort2" : "null", "numberOfResults" : "1", "isNewFeature" : "null", "gfeExclusive" : "0", "webExclusive" : "0", "null,null,null,null,null,null,null,null,null,null,null,null," : "", "cudaToolkitID" : "null", "theSystemIsDc" : "1", "theUserRequestedCRD" : "null"}]}
"""";

    /// <summary>Resposta REAL do DriverManualLookup para psid/pfid inexistentes (Success 0), 01/11/2026.</summary>
    public const string NvidiaLookupNotFound = """"
{ "Success" : "0", "IDS" : [{ "downloadInfo": { "Success" : "0", "ID" : "", "Messaging" : [{"MessageCode" : "DriverDownloadIDNotFound", "MessageValue" : "The Driver Download not found for Manual Lookup requested"}]}}],"spcall" : "EXEC NDL_usp_Download_select_by_matrix_values null,99999,9999,null,135,null,0, null,1046,1,null,null,null,null,null,null,1,null,null,0,null,1,null,0,0,null,null,null,null,null,null,null,null,null,null,null,null,null,1,null", "Request" : [{"downloadID" : "null", "pfid" : "99999", "psid" : "9999", "deviceID" : "null", "osID" : "135", "osCode" : "null", "is64bit" : "0", "languageID" : "null", "languageCode" : "1046", "dltype" : "1", "isArchive" : "null", "beta" : "null", "includebeta" : "null", "gtversion" : "null", "version" : "null", "release" : "null", "isWHQL" : "1", "isNewest" : "null", "locationID" : "null", "sort1" : "0", "sort2" : "null", "numberOfResults" : "1", "isNewFeature" : "null", "gfeExclusive" : "0", "webExclusive" : "0", "null,null,null,null,null,null,null,null,null,null,null,null," : "", "cudaToolkitID" : "null", "theSystemIsDc" : "1", "theUserRequestedCRD" : "null"}]}
"""";

    /// <summary>lookupValueSearch.aspx?TypeID=2 REAL (séries), 01/11/2026.</summary>
    public const string NvidiaSeriesXml = """"
<?xml version="1.0" encoding="UTF-8"?>
<LookupValueSearch>
<LookupValues>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce RTX 50 Series (Notebooks)</Name>
<Value>133</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce RTX 50 Series</Name>
<Value>131</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce RTX 40 Series (Notebooks)</Name>
<Value>129</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce RTX 40 Series</Name>
<Value>127</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce RTX 30 Series (Notebooks)</Name>
<Value>123</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce RTX 30 Series</Name>
<Value>120</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce RTX 20 Series (Notebooks)</Name>
<Value>111</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce RTX 20 Series</Name>
<Value>107</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce MX500 Series (Notebooks)</Name>
<Value>125</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce MX400 Series (Notebooks)</Name>
<Value>121</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce MX300 Series (Notebooks)</Name>
<Value>117</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce MX200 Series (Notebooks)</Name>
<Value>113</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce MX100 Series (Notebook)</Name>
<Value>104</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce GTX 16 Series (Notebooks)</Name>
<Value>115</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 16 Series</Name>
<Value>112</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 10 Series</Name>
<Value>101</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 10 Series (Notebooks)</Name>
<Value>102</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 900 Series</Name>
<Value>98</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 900M Series (Notebooks)</Name>
<Value>99</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 800M Series (Notebooks)</Name>
<Value>97</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 700M Series (Notebooks)</Name>
<Value>92</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 700 Series</Name>
<Value>95</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 600 Series</Name>
<Value>85</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 600M Series (Notebooks)</Name>
<Value>84</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="1" IsSelectLess="False">
<Name>GeForce 500 Series</Name>
<Value>76</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 500M Series (Notebooks)</Name>
<Value>78</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 400 Series</Name>
<Value>71</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="1" IsSelectLess="False">
<Name>GeForce 400M Series (Notebooks)</Name>
<Value>72</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="1" IsSelectLess="False">
<Name>GeForce 300 Series</Name>
<Value>70</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="1" IsSelectLess="False">
<Name>GeForce 300M Series (Notebooks)</Name>
<Value>69</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 200 Series</Name>
<Value>52</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="1" IsSelectLess="False">
<Name>GeForce 200M Series (Notebooks)</Name>
<Value>62</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 100 Series</Name>
<Value>59</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="1" IsSelectLess="False">
<Name>GeForce 100M Series (Notebooks)</Name>
<Value>61</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="1" IsSelectLess="False">
<Name>GeForce 9 Series</Name>
<Value>51</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="1" IsSelectLess="False">
<Name>GeForce 9M Series (Notebooks)</Name>
<Value>53</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 8 Series</Name>
<Value>1</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce 8M Series (Notebooks)</Name>
<Value>54</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="1" IsSelectLess="False">
<Name>GeForce 7 Series</Name>
<Value>2</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="1" IsSelectLess="False">
<Name>GeForce Go 7 Series (Notebooks)</Name>
<Value>55</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="1" IsSelectLess="False">
<Name>GeForce 6 Series</Name>
<Value>3</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="1" IsSelectLess="False">
<Name>GeForce 5 FX Series</Name>
<Value>4</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="False">
<Name>NVIDIA RTX PRO Series</Name>
<Value>132</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="False">
<Name>NVIDIA RTX PRO Series (Notebooks)</Name>
<Value>134</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="True">
<Name>NVIDIA RTX Series</Name>
<Value>122</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="True">
<Name>NVIDIA RTX Series (Notebooks)</Name>
<Value>124</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="True">
<Name>Quadro RTX Series</Name>
<Value>109</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="True">
<Name>Quadro RTX Series (Notebooks)</Name>
<Value>116</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="True">
<Name>Quadro Series</Name>
<Value>73</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="True">
<Name>Quadro Series (Notebooks)</Name>
<Value>74</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="True">
<Name>Quadro FX Series</Name>
<Value>10</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="True">
<Name>Quadro FX Series (Notebooks)</Name>
<Value>42</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="True">
<Name>Quadro Blade/Embedded Series </Name>
<Value>65</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="False">
<Name>Quadro NVS Series</Name>
<Value>11</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="False">
<Name>Quadro NVS Series (Notebooks)</Name>
<Value>39</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="3" IsSelectLess="False">
<Name>Quadro SDI</Name>
<Value>47</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="False">
<Name>Quadro4 XGL Series</Name>
<Value>12</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="3" IsSelectLess="False">
<Name>Quadro DCC Series</Name>
<Value>13</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="False">
<Name>Quadro4 Go Series</Name>
<Value>15</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="3" IsSelectLess="False">
<Name>Quadro2 Go Series</Name>
<Value>16</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="4" IsSelectLess="False">
<Name>nForce 7 Series</Name>
<Value>45</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>GeForce 4 MX Series</Name>
<Value>18</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>GeForce 4 Ti Series</Name>
<Value>17</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="4" IsSelectLess="False">
<Name>nForce 6 Series</Name>
<Value>5</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="4" IsSelectLess="False">
<Name>nForce 5 Series</Name>
<Value>6</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>GeForce 3 Series</Name>
<Value>19</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="4" IsSelectLess="False">
<Name>GeForce 2 GTS/Ultra/Pro</Name>
<Value>20</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>nForce Professional 2000 Series</Name>
<Value>43</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>nForce Professional 3000 Series</Name>
<Value>44</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="4" IsSelectLess="False">
<Name>GeForce 2 MX Series</Name>
<Value>21</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>GeForce 256 Series</Name>
<Value>22</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>TNT and TNT2 Series</Name>
<Value>25</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>Riva 128 PCI Series</Name>
<Value>23</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>Riva 128 AGP Series</Name>
<Value>24</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>Vanta Series</Name>
<Value>26</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>Quadro 2 Pro Series</Name>
<Value>27</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>Quadro 2 MXR Series</Name>
<Value>28</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="4" IsSelectLess="False">
<Name>nForce 9 Series</Name>
<Value>58</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>nForce 1/2 Series</Name>
<Value>36</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>nForce 3 Series</Name>
<Value>37</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="4" IsSelectLess="False">
<Name>nForce 4 Series</Name>
<Value>7</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="4" IsSelectLess="False">
<Name>nForce Audio</Name>
<Value>35</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="5" IsSelectLess="False">
<Name>3D Vision</Name>
<Value>57</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="5" IsSelectLess="False">
<Name>3D Vision Pro</Name>
<Value>64</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="6" IsSelectLess="False">
<Name>ION (Desktops)</Name>
<Value>63</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="6" IsSelectLess="False">
<Name>ION (Notebooks)</Name>
<Value>67</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="6" IsSelectLess="False">
<Name>ION LE (Desktops)</Name>
<Value>66</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="6" IsSelectLess="False">
<Name>ION LE (Notebooks)</Name>
<Value>68</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>B-Series</Name>
<Value>135</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>L-Series</Name>
<Value>128</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>H-Series</Name>
<Value>126</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="True">
<Name>A-Series</Name>
<Value>118</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>RTX-Series</Name>
<Value>119</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>HGX-Series</Name>
<Value>114</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>T-Series</Name>
<Value>110</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>V-Series</Name>
<Value>105</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>P-Series</Name>
<Value>103</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>K-Series</Name>
<Value>91</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>C-Class</Name>
<Value>48</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>M-Class</Name>
<Value>75</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>S-Class</Name>
<Value>50</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="7" IsSelectLess="False">
<Name>X-Class</Name>
<Value>83</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="8" IsSelectLess="False">
<Name>NVS Series</Name>
<Value>77</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="8" IsSelectLess="False">
<Name>NVS Series (Notebooks)</Name>
<Value>80</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="9" IsSelectLess="False">
<Name>NVIDIA GRID vGPU</Name>
<Value>96</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="9" IsSelectLess="False">
<Name>GRID Series</Name>
<Value>94</Value>
</LookupValue>
<LookupValue RequiresProduct="False" ParentID="10" IsSelectLess="False">
<Name>CUDA Billboard Semaphore</Name>
<Value>93</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="11" IsSelectLess="False">
<Name>NVIDIA TITAN Series</Name>
<Value>106</Value>
</LookupValue>
<LookupValue RequiresProduct="True" ParentID="12" IsSelectLess="False">
<Name>BlueField DPU</Name>
<Value>130</Value>
</LookupValue>
</LookupValues>
</LookupValueSearch>
"""";

    /// <summary>lookupValueSearch.aspx?TypeID=3 REAL, recortado às séries GeForce 10/16/30/40 (desktop e notebook) e duas linhas de outra família, 01/11/2026.</summary>
    public const string NvidiaProductsXml = """"
<?xml version="1.0" encoding="UTF-8"?><LookupValueSearch><LookupValues><LookupValue ParentID="73">
<Name>Quadro GV100</Name>
<Value>866</Value>
</LookupValue>
<LookupValue ParentID="73">
<Name>Quadro GP100</Name>
<Value>839</Value>
</LookupValue>
<LookupValue ParentID="101">
<Name>GeForce GTX 1080 Ti</Name>
<Value>845</Value>
</LookupValue>
<LookupValue ParentID="101">
<Name>GeForce GTX 1080</Name>
<Value>815</Value>
</LookupValue>
<LookupValue ParentID="101">
<Name>GeForce GTX 1070 Ti</Name>
<Value>859</Value>
</LookupValue>
<LookupValue ParentID="101">
<Name>GeForce GTX 1070</Name>
<Value>816</Value>
</LookupValue>
<LookupValue ParentID="101">
<Name>GeForce GTX 1060</Name>
<Value>817</Value>
</LookupValue>
<LookupValue ParentID="101">
<Name>GeForce GTX 1050 Ti</Name>
<Value>825</Value>
</LookupValue>
<LookupValue ParentID="101">
<Name>GeForce GTX 1050</Name>
<Value>826</Value>
</LookupValue>
<LookupValue ParentID="101">
<Name>GeForce GT 1030</Name>
<Value>852</Value>
</LookupValue>
<LookupValue ParentID="101">
<Name>GeForce GT 1010</Name>
<Value>936</Value>
</LookupValue>
<LookupValue ParentID="102">
<Name>GeForce GTX 1080</Name>
<Value>819</Value>
</LookupValue>
<LookupValue ParentID="102">
<Name>GeForce GTX 1070</Name>
<Value>820</Value>
</LookupValue>
<LookupValue ParentID="102">
<Name>GeForce GTX 1060</Name>
<Value>821</Value>
</LookupValue>
<LookupValue ParentID="102">
<Name>GeForce GTX 1050 Ti</Name>
<Value>836</Value>
</LookupValue>
<LookupValue ParentID="102">
<Name>GeForce GTX 1050</Name>
<Value>837</Value>
</LookupValue>
<LookupValue ParentID="112">
<Name>GeForce GTX 1660 SUPER</Name>
<Value>910</Value>
</LookupValue>
<LookupValue ParentID="112">
<Name>GeForce GTX 1650 SUPER</Name>
<Value>911</Value>
</LookupValue>
<LookupValue ParentID="112">
<Name>GeForce GTX 1660 Ti</Name>
<Value>892</Value>
</LookupValue>
<LookupValue ParentID="112">
<Name>GeForce GTX 1660</Name>
<Value>895</Value>
</LookupValue>
<LookupValue ParentID="112">
<Name>GeForce GTX 1650</Name>
<Value>897</Value>
</LookupValue>
<LookupValue ParentID="112">
<Name>GeForce GTX 1630</Name>
<Value>993</Value>
</LookupValue>
<LookupValue ParentID="115">
<Name>GeForce GTX 1660 Ti</Name>
<Value>899</Value>
</LookupValue>
<LookupValue ParentID="115">
<Name>GeForce GTX 1650 Ti</Name>
<Value>921</Value>
</LookupValue>
<LookupValue ParentID="115">
<Name>GeForce GTX 1650</Name>
<Value>898</Value>
</LookupValue>
<LookupValue ParentID="123">
<Name>GeForce RTX 3080 Ti Laptop GPU</Name>
<Value>976</Value>
</LookupValue>
<LookupValue ParentID="123">
<Name>GeForce RTX 3080 Laptop GPU</Name>
<Value>938</Value>
</LookupValue>
<LookupValue ParentID="123">
<Name>GeForce RTX 3070 Ti Laptop GPU</Name>
<Value>979</Value>
</LookupValue>
<LookupValue ParentID="123">
<Name>GeForce RTX 3070 Laptop GPU</Name>
<Value>939</Value>
</LookupValue>
<LookupValue ParentID="123">
<Name>GeForce RTX 3060 Laptop GPU</Name>
<Value>940</Value>
</LookupValue>
<LookupValue ParentID="123">
<Name>GeForce RTX 3050 Ti Laptop GPU</Name>
<Value>962</Value>
</LookupValue>
<LookupValue ParentID="123">
<Name>GeForce RTX 3050 Laptop GPU</Name>
<Value>963</Value>
</LookupValue>
<LookupValue ParentID="127">
<Name>NVIDIA GeForce RTX 4090 D</Name>
<Value>1036</Value>
</LookupValue>
<LookupValue ParentID="127">
<Name>NVIDIA GeForce RTX 4090</Name>
<Value>995</Value>
</LookupValue>
<LookupValue ParentID="127">
<Name>NVIDIA GeForce RTX 4080 SUPER</Name>
<Value>1041</Value>
</LookupValue>
<LookupValue ParentID="127">
<Name>NVIDIA GeForce RTX 4080</Name>
<Value>999</Value>
</LookupValue>
<LookupValue ParentID="127">
<Name>NVIDIA GeForce RTX 4070 Ti SUPER</Name>
<Value>1040</Value>
</LookupValue>
<LookupValue ParentID="127">
<Name>NVIDIA GeForce RTX 4070 Ti</Name>
<Value>1001</Value>
</LookupValue>
<LookupValue ParentID="127">
<Name>NVIDIA GeForce RTX 4070 SUPER</Name>
<Value>1039</Value>
</LookupValue>
<LookupValue ParentID="127">
<Name>NVIDIA GeForce RTX 4070</Name>
<Value>1015</Value>
</LookupValue>
<LookupValue ParentID="127">
<Name>NVIDIA GeForce RTX 4060 Ti</Name>
<Value>1022</Value>
</LookupValue>
<LookupValue ParentID="127">
<Name>NVIDIA GeForce RTX 4060</Name>
<Value>1023</Value>
</LookupValue>
<LookupValue ParentID="129">
<Name>GeForce RTX 4090 Laptop GPU</Name>
<Value>1004</Value>
</LookupValue>
<LookupValue ParentID="129">
<Name>GeForce RTX 4080 Laptop GPU</Name>
<Value>1005</Value>
</LookupValue>
<LookupValue ParentID="129">
<Name>GeForce RTX 4070 Laptop GPU</Name>
<Value>1006</Value>
</LookupValue>
<LookupValue ParentID="129">
<Name>GeForce RTX 4060 Laptop GPU</Name>
<Value>1007</Value>
</LookupValue>
<LookupValue ParentID="129">
<Name>GeForce RTX 4050 Laptop GPU</Name>
<Value>1008</Value>
</LookupValue>
</LookupValues></LookupValueSearch>
"""";

    /// <summary>Página REAL de drivers do Ryzen 5 5600H no amd.com (recorte das seções Windows 11/10), 01/11/2026.</summary>
    public const string AmdRyzen5600hPage = """"
<div class="accordion-item" id="accordion-content-component-6ba1c4d41d-category-0">
 <h2 class="accordion-header">
 <button class="accordion-button collapsed" type="button" id="content-component-6ba1c4d41d-category-0-button" data-bs-toggle="collapse" data-bs-target="#content-component-6ba1c4d41d-panel-0" aria-expanded="false" aria-controls="content-component-6ba1c4d41d-panel-0">
 Windows 11 - 64-Bit Edition
 </button>
 </h2>
 <div id="content-component-6ba1c4d41d-panel-0" class="accordion-collapse collapse" data-bs-parent="#content-component-6ba1c4d41d-category" role="region" aria-labelledby="content-component-6ba1c4d41d-category-0-button">
 <div class="accordion-body">
 <div id="subcategory-content-component-6ba1c4d41d-content-component-6ba1c4d41d-panel-0-0" class="category">
 <h3 class="category-title">
 Driver
 </h3>
 <article class="container-fluid driver-download-details" style="background-color: rgb(241, 241, 242);">
 <div class="row">
 <div class="col-12">
 <h4>AMD Software: Adrenalin Edition</h4>
 </div>
 <div class="col-6 col-lg">
 <strong>Revision Number</strong>
 <p>Adrenalin 26.5.2 (WHQL Recommended)</p>
 </div>
 <div class="col-6 col-lg">
 <strong>File Size</strong>
 <p>623 MB</p>
 </div>
 <div class="col-6 col-lg">
 <strong>Release Date</strong>
 <p>
 2026-05-21
 </p>
 </div>
 <div class="col-6 col-lg">
 <div class="button btn btn-Primary-Black">
 <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-win11-may-vega-polaris.exe" aria-label="Download AMD Software: Adrenalin Edition" role="button">
 <span>
 Download<sup>*</sup> 
 <span class="visually-hidden">AMD Software: Adrenalin Edition</span>
 <i class="fa-sharp fa-solid fa-download" aria-hidden="true"></i>
 </span>
 </a>
 </div>
 </div>
 <div class="col-12">
 <a href="#collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0" data-bs-toggle="collapse" class="driver-expand-collapse" data-bs-target="#collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0" aria-expanded="false" aria-controls="collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0" aria-label="Toggle driver details for AMD Software: Adrenalin Edition" role="button">
 Driver Details
 <i class="fass fa-chevron-down ms-2" aria-hidden="true"></i>
 </a>
 <div class="collapse" id="collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0">
 <div class="card card-body mt-2">
 <p><a href="https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-26-5-2-POLARIS-VEGA.html">​​​​​​​​​Release Notes</a></p>
<p><b>Important Note for Laptop and All-In-One (AIO) PCs</b></p>
<p>AMD recommends OEM-provided drivers which are customized and validated for their system-specific features and optimizations.</p>
<p>If you experience issues using the AMD Software: Adrenalin Edition driver package downloaded from AMD.com, please install the OEM-provided drivers for full support and compatibility.</p>
 </div>
 </div>
 </div>
 </div>
 </article>
 </div>
 </div>
 </div>
 </div>
 <div class="accordion-item" id="accordion-content-component-6ba1c4d41d-category-1">
 <h2 class="accordion-header">
 <button class="accordion-button collapsed" type="button" id="content-component-6ba1c4d41d-category-1-button" data-bs-toggle="collapse" data-bs-target="#content-component-6ba1c4d41d-panel-1" aria-expanded="false" aria-controls="content-component-6ba1c4d41d-panel-1">
 Windows 10 - 64-Bit Edition
 </button>
 </h2>
 <div id="content-component-6ba1c4d41d-panel-1" class="accordion-collapse collapse" data-bs-parent="#content-component-6ba1c4d41d-category" role="region" aria-labelledby="content-component-6ba1c4d41d-category-1-button">
 <div class="accordion-body">
 <div id="subcategory-content-component-6ba1c4d41d-content-component-6ba1c4d41d-panel-1-0" class="category">
 <h3 class="category-title">
 Driver
 </h3>
 <article class="container-fluid driver-download-details" style="background-color: rgb(241, 241, 242);">
 <div class="row">
 <div class="col-12">
 <h4>AMD Software: Adrenalin Edition</h4>
 </div>
 <div class="col-6 col-lg">
 <strong>Revision Number</strong>
 <p>Adrenalin 26.5.2 (WHQL Recommended)</p>
 </div>
 <div class="col-6 col-lg">
 <strong>File Size</strong>
 <p>623 MB</p>
 </div>
 <div class="col-6 col-lg">
 <strong>Release Date</strong>
 <p>
 2026-05-21
 </p>
 </div>
 <div class="col-6 col-lg">
 <div class="button btn btn-Primary-Black">
 <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-win11-may-vega-polaris.exe" aria-label="Download AMD Software: Adrenalin Edition" role="button">
 <span>
 Download<sup>*</sup> 
 <span class="visually-hidden">AMD Software: Adrenalin Edition</span>
 <i class="fa-sharp fa-solid fa-download" aria-hidden="true"></i>
 </span>
 </a>
 </div>
 </div>
 <div class="col-12">
 <a href="#collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0" data-bs-toggle="collapse" class="driver-expand-collapse" data-bs-target="#collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0" aria-expanded="false" aria-controls="collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0" aria-label="Toggle driver details for AMD Software: Adrenalin Edition" role="button">
 Driver Details
 <i class="fass fa-chevron-down ms-2" aria-hidden="true"></i>
 </a>
 <div class="collapse" id="collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0">
 <div class="card card-body mt-2">
 <p><a href="https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-26-5-2-POLARIS-VEGA.html">​​​​​​​​​Release Notes</a></p>
<p><b>Important Note for Laptop and All-In-One (AIO) PCs</b></p>
<p>AMD recommends OEM-provided drivers which are customized and validated for their system-specific features and optimizations.</p>
<p>If you experience issues using the AMD Software: Adrenalin Edition driver package downloaded from AMD.com, please install the OEM-provided drivers for full support and compatibility.</p>
 </div>
 </div>
 </div>
 </div>
 </article>
 </div>
 </div>
 </div>
 </div>
 </div>
 </section>
 <div>
<div id="text-e975801a7b" class="cmp-text">
 <p><br />
*By clicking the &quot;Download&quot; button, you are confirming that you have read and agree to be bound by the <a href="/en/legal/eula.html">End User License Agreement</a>. Your download will begin immediately after clicking on the &quot;Download&quot; button. If you do not agree to these terms and conditions, you do not have a license or permission to use and/or download the software.</p>
</div>
</div>
 <div class="button btn btn-Solid-Black">
 <a href="https://www.amd.com/en/support/downloads/previous-drivers.html/processors/ryzen/ryzen-5000-series/amd-ryzen-5-5600h.html">
"""";

    /// <summary>Página REAL de drivers da Radeon RX 7600 no amd.com (recorte das seções Windows 11/10), 01/11/2026.</summary>
    public const string AmdRx7600Page = """"
<div class="accordion-item" id="accordion-content-component-6ba1c4d41d-category-0">
 <h2 class="accordion-header">
 <button class="accordion-button collapsed" type="button" id="content-component-6ba1c4d41d-category-0-button" data-bs-toggle="collapse" data-bs-target="#content-component-6ba1c4d41d-panel-0" aria-expanded="false" aria-controls="content-component-6ba1c4d41d-panel-0">
 Windows 11 - 64-Bit Edition
 </button>
 </h2>
 <div id="content-component-6ba1c4d41d-panel-0" class="accordion-collapse collapse" data-bs-parent="#content-component-6ba1c4d41d-category" role="region" aria-labelledby="content-component-6ba1c4d41d-category-0-button">
 <div class="accordion-body">
 <div id="subcategory-content-component-6ba1c4d41d-content-component-6ba1c4d41d-panel-0-0" class="category">
 <h3 class="category-title">
 Driver
 </h3>
 <article class="container-fluid driver-download-details" style="background-color: rgb(241, 241, 242);">
 <div class="row">
 <div class="col-12">
 <h4>Auto-Detect and Install</h4>
 </div>
 <div class="col-6 col-lg">
 <strong>Revision Number</strong>
 </div>
 <div class="col-6 col-lg">
 <strong>File Size</strong>
 <p>46 MB</p>
 </div>
 <div class="col-6 col-lg">
 <strong>Release Date</strong>
 <p>
 2026-10-30
 </p>
 </div>
 <div class="col-6 col-lg">
 <div class="button btn btn-Primary-Black">
 <a href="https://drivers.amd.com/drivers/installer/26.20/beta/amd-software-adrenalin-edition-26.10.2-minimalsetup-261029_web.exe" aria-label="Download Auto-Detect and Install" role="button">
 <span>
 Download<sup>*</sup> 
 <span class="visually-hidden">Auto-Detect and Install</span>
 <i class="fa-sharp fa-solid fa-download" aria-hidden="true"></i>
 </span>
 </a>
 </div>
 </div>
 <div class="col-12">
 <a href="#collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0" data-bs-toggle="collapse" class="driver-expand-collapse" data-bs-target="#collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0" aria-expanded="false" aria-controls="collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0" aria-label="Toggle driver details for Auto-Detect and Install" role="button">
 Driver Details
 <i class="fass fa-chevron-down ms-2" aria-hidden="true"></i>
 </a>
 <div class="collapse" id="collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0">
 <div class="card card-body mt-2">
 <p>For use with systems equipped with AMD Radeon™ discrete desktop graphics, mobile graphics, or AMD processors with Radeon graphics. This tool is designed to detect the model of AMD graphics card and the version of Microsoft® Windows© installed in your system, and then provide the option to download and install the latest official AMD driver package that is compatible with your system. Download and run directly onto the system you want to update. An internet connection is required to run the tool. <a data-entity-uuid="8cc105c3-dc2b-4603-a540-fe21ed87ba1d" href="/en/resources/support-articles/faqs/GPU-Driver-Autodetect.html">Learn More</a></p>
 </div>
 </div>
 </div>
 </div>
 </article>
 <article class="container-fluid driver-download-details" style="background-color: rgb(241, 241, 242);">
 <div class="row">
 <div class="col-12">
 <h4>AMD Software: Adrenalin Edition</h4>
 </div>
 <div class="col-6 col-lg">
 <strong>Revision Number</strong>
 <p>Adrenalin 26.10.2 WHQL (Optional)</p>
 </div>
 <div class="col-6 col-lg">
 <strong>File Size</strong>
 <p>979 MB</p>
 </div>
 <div class="col-6 col-lg">
 <strong>Release Date</strong>
 <p>
 2026-10-30
 </p>
 </div>
 <div class="col-6 col-lg">
 <div class="button btn btn-Primary-Black">
 <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.10.2-win11-b.exe" aria-label="Download AMD Software: Adrenalin Edition" role="button">
 <span>
 Download<sup>*</sup> 
 <span class="visually-hidden">AMD Software: Adrenalin Edition</span>
 <i class="fa-sharp fa-solid fa-download" aria-hidden="true"></i>
 </span>
 </a>
 </div>
 </div>
 <div class="col-12">
 <a href="#collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0-1" data-bs-toggle="collapse" class="driver-expand-collapse" data-bs-target="#collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0-1" aria-expanded="false" aria-controls="collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0-1" aria-label="Toggle driver details for AMD Software: Adrenalin Edition" role="button">
 Driver Details
 <i class="fass fa-chevron-down ms-2" aria-hidden="true"></i>
 </a>
 <div class="collapse" id="collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0-1">
 <div class="card card-body mt-2">
 <p><a href="/en/resources/support-articles/release-notes/RN-RAD-WIN-26-10-2.html" title="AMD Software: Adrenalin Edition 24.9.1 Release Notes">Release No​tes</a>​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​</p>
 </div>
 </div>
 </div>
 </div>
 </article>
 <article class="container-fluid driver-download-details" style="background-color: rgb(241, 241, 242);">
 <div class="row">
 <div class="col-12">
 <h4>AMD Software: Adrenalin Edition</h4>
 </div>
 <div class="col-6 col-lg">
 <strong>Revision Number</strong>
 <p>Adrenalin 26.10.1 WHQL (Recommended)</p>
 </div>
 <div class="col-6 col-lg">
 <strong>File Size</strong>
 <p>887 MB</p>
 </div>
 <div class="col-6 col-lg">
 <strong>Release Date</strong>
 <p>
 2026-10-20
 </p>
 </div>
 <div class="col-6 col-lg">
 <div class="button btn btn-Primary-Black">
 <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.10.1-win11-b.exe" aria-label="Download AMD Software: Adrenalin Edition" role="button">
 <span>
 Download<sup>*</sup> 
 <span class="visually-hidden">AMD Software: Adrenalin Edition</span>
 <i class="fa-sharp fa-solid fa-download" aria-hidden="true"></i>
 </span>
 </a>
 </div>
 </div>
 <div class="col-12">
 <a href="#collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0-1-2" data-bs-toggle="collapse" class="driver-expand-collapse" data-bs-target="#collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0-1-2" aria-expanded="false" aria-controls="collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0-1-2" aria-label="Toggle driver details for AMD Software: Adrenalin Edition" role="button">
 Driver Details
 <i class="fass fa-chevron-down ms-2" aria-hidden="true"></i>
 </a>
 <div class="collapse" id="collapsedriverdetails-content-component-6ba1c4d41d-panel-0-0-0-1-2">
 <div class="card card-body mt-2">
 <p><a href="/en/resources/support-articles/release-notes/RN-RAD-WIN-26-10-1.html" title="AMD Software: Adrenalin Edition 24.9.1 Release Notes">Release No​tes</a>​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​</p>
 </div>
 </div>
 </div>
 </div>
 </article>
 </div>
 </div>
 </div>
 </div>
 <div class="accordion-item" id="accordion-content-component-6ba1c4d41d-category-1">
 <h2 class="accordion-header">
 <button class="accordion-button collapsed" type="button" id="content-component-6ba1c4d41d-category-1-button" data-bs-toggle="collapse" data-bs-target="#content-component-6ba1c4d41d-panel-1" aria-expanded="false" aria-controls="content-component-6ba1c4d41d-panel-1">
 Windows 10 - 64-Bit Edition
 </button>
 </h2>
 <div id="content-component-6ba1c4d41d-panel-1" class="accordion-collapse collapse" data-bs-parent="#content-component-6ba1c4d41d-category" role="region" aria-labelledby="content-component-6ba1c4d41d-category-1-button">
 <div class="accordion-body">
 <div id="subcategory-content-component-6ba1c4d41d-content-component-6ba1c4d41d-panel-1-0" class="category">
 <h3 class="category-title">
 Driver
 </h3>
 <article class="container-fluid driver-download-details" style="background-color: rgb(241, 241, 242);">
 <div class="row">
 <div class="col-12">
 <h4>Auto-Detect and Install</h4>
 </div>
 <div class="col-6 col-lg">
 <strong>Revision Number</strong>
 </div>
 <div class="col-6 col-lg">
 <strong>File Size</strong>
 <p>46 MB</p>
 </div>
 <div class="col-6 col-lg">
 <strong>Release Date</strong>
 <p>
 2026-10-30
 </p>
 </div>
 <div class="col-6 col-lg">
 <div class="button btn btn-Primary-Black">
 <a href="https://drivers.amd.com/drivers/installer/26.20/beta/amd-software-adrenalin-edition-26.10.2-minimalsetup-261029_web.exe" aria-label="Download Auto-Detect and Install" role="button">
 <span>
 Download<sup>*</sup> 
 <span class="visually-hidden">Auto-Detect and Install</span>
 <i class="fa-sharp fa-solid fa-download" aria-hidden="true"></i>
 </span>
 </a>
 </div>
 </div>
 <div class="col-12">
 <a href="#collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0" data-bs-toggle="collapse" class="driver-expand-collapse" data-bs-target="#collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0" aria-expanded="false" aria-controls="collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0" aria-label="Toggle driver details for Auto-Detect and Install" role="button">
 Driver Details
 <i class="fass fa-chevron-down ms-2" aria-hidden="true"></i>
 </a>
 <div class="collapse" id="collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0">
 <div class="card card-body mt-2">
 <p>For use with systems equipped with AMD Radeon™ discrete desktop graphics, mobile graphics, or AMD processors with Radeon graphics. This tool is designed to detect the model of AMD graphics card and the version of Microsoft® Windows© installed in your system, and then provide the option to download and install the latest official AMD driver package that is compatible with your system. Download and run directly onto the system you want to update. An internet connection is required to run the tool. <a data-entity-uuid="8cc105c3-dc2b-4603-a540-fe21ed87ba1d" href="/en/resources/support-articles/faqs/GPU-Driver-Autodetect.html">Learn More</a></p>
 </div>
 </div>
 </div>
 </div>
 </article>
 <article class="container-fluid driver-download-details" style="background-color: rgb(241, 241, 242);">
 <div class="row">
 <div class="col-12">
 <h4>AMD Software: Adrenalin Edition</h4>
 </div>
 <div class="col-6 col-lg">
 <strong>Revision Number</strong>
 <p>Adrenalin 26.10.2 WHQL (Optional)</p>
 </div>
 <div class="col-6 col-lg">
 <strong>File Size</strong>
 <p>979 MB</p>
 </div>
 <div class="col-6 col-lg">
 <strong>Release Date</strong>
 <p>
 2026-10-30
 </p>
 </div>
 <div class="col-6 col-lg">
 <div class="button btn btn-Primary-Black">
 <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.10.2-win11-b.exe" aria-label="Download AMD Software: Adrenalin Edition" role="button">
 <span>
 Download<sup>*</sup> 
 <span class="visually-hidden">AMD Software: Adrenalin Edition</span>
 <i class="fa-sharp fa-solid fa-download" aria-hidden="true"></i>
 </span>
 </a>
 </div>
 </div>
 <div class="col-12">
 <a href="#collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0-1" data-bs-toggle="collapse" class="driver-expand-collapse" data-bs-target="#collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0-1" aria-expanded="false" aria-controls="collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0-1" aria-label="Toggle driver details for AMD Software: Adrenalin Edition" role="button">
 Driver Details
 <i class="fass fa-chevron-down ms-2" aria-hidden="true"></i>
 </a>
 <div class="collapse" id="collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0-1">
 <div class="card card-body mt-2">
 <p><a href="/en/resources/support-articles/release-notes/RN-RAD-WIN-26-10-2.html" title="AMD Software: Adrenalin Edition 24.9.1 Release Notes">Release No​tes</a>​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​</p>
 </div>
 </div>
 </div>
 </div>
 </article>
 <article class="container-fluid driver-download-details" style="background-color: rgb(241, 241, 242);">
 <div class="row">
 <div class="col-12">
 <h4>AMD Software: Adrenalin Edition</h4>
 </div>
 <div class="col-6 col-lg">
 <strong>Revision Number</strong>
 <p>Adrenalin 26.10.1 WHQL (Recommended)</p>
 </div>
 <div class="col-6 col-lg">
 <strong>File Size</strong>
 <p>887 MB</p>
 </div>
 <div class="col-6 col-lg">
 <strong>Release Date</strong>
 <p>
 2026-10-20
 </p>
 </div>
 <div class="col-6 col-lg">
 <div class="button btn btn-Primary-Black">
 <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.10.1-win11-b.exe" aria-label="Download AMD Software: Adrenalin Edition" role="button">
 <span>
 Download<sup>*</sup> 
 <span class="visually-hidden">AMD Software: Adrenalin Edition</span>
 <i class="fa-sharp fa-solid fa-download" aria-hidden="true"></i>
 </span>
 </a>
 </div>
 </div>
 <div class="col-12">
 <a href="#collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0-1-2" data-bs-toggle="collapse" class="driver-expand-collapse" data-bs-target="#collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0-1-2" aria-expanded="false" aria-controls="collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0-1-2" aria-label="Toggle driver details for AMD Software: Adrenalin Edition" role="button">
 Driver Details
 <i class="fass fa-chevron-down ms-2" aria-hidden="true"></i>
 </a>
 <div class="collapse" id="collapsedriverdetails-content-component-6ba1c4d41d-panel-1-0-0-1-2">
 <div class="card card-body mt-2">
 <p><a href="/en/resources/support-articles/release-notes/RN-RAD-WIN-26-10-1.html" title="AMD Software: Adrenalin Edition 24.9.1 Release Notes">Release No​tes</a>​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​​</p>
 </div>
 </div>
 </div>
 </div>
 </article>
 </div>
 </div>
 </div>
 </div>
 <div class="
"""";
}
