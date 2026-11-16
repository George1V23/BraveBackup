// Independent synthetic fixture producer; see docs/tabmanagerplus-research.md.
// Requires the pinned upstream Google LevelDB, only for fixture regeneration.
#include <leveldb/db.h>
#include <leveldb/options.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>

static void check(const leveldb::Status& status) {
  if (!status.ok()) throw std::runtime_error(status.ToString());
}

static std::unique_ptr<leveldb::DB> open(const std::filesystem::path& path) {
  leveldb::Options options;
  options.create_if_missing = true;
  options.paranoid_checks = true;
  options.compression = leveldb::kSnappyCompression;
  leveldb::DB* raw = nullptr;
  check(leveldb::DB::Open(options, path.string(), &raw));
  return std::unique_ptr<leveldb::DB>(raw);
}

int main(int argc, char** argv) {
  try {
    if (argc != 2) throw std::runtime_error("Pass a new output directory");
    const std::filesystem::path root(argv[1]);
    if (std::filesystem::exists(root))
      throw std::runtime_error("Output must not already exist");
    std::filesystem::create_directories(root);
    // Application-shaped records, not copied personal data or an upstream export.
    const std::string latest = R"({"fixture-window":{"id":"fixture-window","name":"Synthetic work","customName":true,"color":"blue","date":1700000000000,"sessionStartTime":1700000000000,"incognito":false,"order":0,"windowsInfo":{"id":7,"type":"normal","state":"normal","width":1200,"height":800},"tabs":[{"index":0,"url":"https://example.invalid/current","title":"Synthetic tab","pinned":true,"active":true}]}})";
    leveldb::WriteOptions write;
    write.sync = true;
    for (const std::string kind : {"table-and-wal", "deleted", "empty"}) {
      auto db = open(root / kind);
      check(db->Put(write, "version", "\"7.0.0\""));
      check(db->Put(write, "sessions", "{\"obsolete\":{\"tabs\":[]}}"));
      // Force Snappy-compressible table data, then put the newer value in WAL.
      check(db->Put(write, "fixture-padding", "\"" + std::string(16000, 'x') + "\""));
      db->CompactRange(nullptr, nullptr);
      if (kind == "deleted") check(db->Delete(write, "sessions"));
      else check(db->Put(write, "sessions", kind == "empty" ? "{}" : latest));
      std::string value;
      auto status = db->Get(leveldb::ReadOptions(), "sessions", &value);
      if (kind == "deleted") {
        if (!status.IsNotFound()) throw std::runtime_error("Deletion verification failed");
        std::ofstream(root / (kind + ".expected.json")) << "{\"found\":false}\n";
      } else {
        check(status);
        if (value != (kind == "empty" ? "{}" : latest))
          throw std::runtime_error("Value verification failed");
        std::ofstream(root / (kind + ".expected.json"))
            << "{\"found\":true,\"value\":" << value << "}\n";
      }
      db.reset();
      // Runtime diagnostics and locks carry no fixture meaning. Manifest/WAL/
      // tables/CURRENT remain byte-for-byte as emitted by LevelDB.
      for (const auto& file : std::filesystem::directory_iterator(root / kind)) {
        auto name = file.path().filename().string();
        if (name == "LOCK" || name == "LOG" || name == "LOG.old")
          std::filesystem::remove(file.path());
      }
      std::cout << kind << ": verified by upstream DB::Get\n";
    }
  } catch (const std::exception& error) {
    std::cerr << error.what() << '\n';
    return 1;
  }
}
